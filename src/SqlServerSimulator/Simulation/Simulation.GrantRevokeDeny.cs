using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and applies <c>GRANT</c> / <c>REVOKE</c> / <c>DENY</c>. The
    /// <c>ON</c> securable resolves to a real (class, major_id): a bare or
    /// <c>OBJECT::</c> name to class 1 + the object's id, <c>SCHEMA::name</c>
    /// to class 3 + schema id, <c>USER::name</c> to class 4 + principal id, no
    /// <c>ON</c> clause / <c>DATABASE::name</c> to class 0. The stored row's
    /// grantor is the granting session's effective principal, and the writer
    /// honors <c>WITH GRANT OPTION</c> (single <c>W</c> row), CASCADE, and the
    /// delegated-authority rules (see the per-branch remarks).
    /// </summary>
    internal static bool TryParseGrantRevokeDeny(ParserContext context, PermissionStatementKind kind)
    {
        // Cursor on GRANT / REVOKE / DENY.
        context.MoveNextRequired();

        // REVOKE may carry a GRANT OPTION FOR clause before the permission
        // list (REVOKE GRANT OPTION FOR perm …). Consume-and-track.
        var revokeGrantOptionOnly = false;
        if (kind == PermissionStatementKind.Revoke && context.Token is ReservedKeyword { Keyword: Keyword.Grant })
        {
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Option })
            {
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.For })
                {
                    context.MoveNextRequired();
                    revokeGrantOptionOnly = true;
                }
                else
                {
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                }
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }

        // Permission list — comma-separated spelled-out permission names, each
        // optionally followed by a parenthesized column list
        // (<c>SELECT (a, b)</c>) for the column-level grant forms. Each
        // permission is a sequence of one or more bare identifier tokens; the
        // sequence ends at a comma, '(', ON, TO, FROM, or AS.
        var permissions = new List<(string Name, List<string>? Columns)>();
        var currentTokens = new List<string>();
        while (true)
        {
            var word = TryConsumePermissionWord(context);
            if (word is not null)
            {
                currentTokens.Add(word);
                context.MoveNextRequired();
                continue;
            }
            if (currentTokens.Count > 0)
            {
                var permName = string.Join(" ", currentTokens);
                currentTokens.Clear();
                var columns = context.Token is Operator { Character: '(' } ? ParsePermissionColumnList(context) : null;
                permissions.Add((permName, columns));
            }
            if (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                continue;
            }
            break;
        }
        if (permissions.Count == 0)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Optional ON <securable>. Forms: ON name, ON OBJECT::name,
        // ON SCHEMA::name, ON DATABASE::name, ON USER::name, ON TYPE::name.
        byte permClass = 0;
        var permMajorId = 0;
        var securableDisplayName = context.CurrentDatabase.Name;
        MultiPartName? userSecurableName = null;
        MultiPartName? objectSecurableName = null;
        List<string>? objectColumns = null;
        var hadOnClause = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            hadOnClause = true;
            context.MoveNextRequired();
            var classWord = context.Token switch
            {
                ReservedKeyword { Keyword: Keyword.User } => "USER",
                ReservedKeyword { Keyword: Keyword.Database } => "DATABASE",
                ReservedKeyword { Keyword: Keyword.Schema } => "SCHEMA",
                Name named => named.Value,
                _ => null,
            };
            var explicitClass = (string?)null;
            if (classWord is not null)
            {
                var checkpoint = context.SaveCheckpoint();
                _ = context.GetNextOptional();
                if (context.Token is Operator { Character: ':' }
                    && context.GetNextOptional() is Operator { Character: ':' })
                {
                    context.MoveNextRequired();
                    explicitClass = classWord.ToUpperInvariant();
                }
                else
                {
                    context.RestoreCheckpoint(checkpoint);
                }
            }
            var securableName = BatchContext.ParseObjectName(context);
            context.MoveNextRequired();
            securableDisplayName = securableName.Leaf;
            // Column list on the securable (GRANT SELECT ON t (a, b)) — the
            // alternate placement of the column-level form, applying to every
            // permission in the statement.
            if (context.Token is Operator { Character: '(' })
                objectColumns = ParsePermissionColumnList(context);
            switch (explicitClass)
            {
                case "DATABASE":
                    permClass = PermissionChecker.ClassDatabase;
                    break;
                case "SCHEMA":
                    permClass = PermissionChecker.ClassSchema;
                    objectSecurableName = securableName;
                    break;
                case "USER":
                    permClass = PermissionChecker.ClassDatabasePrincipal;
                    userSecurableName = securableName;
                    break;
                default:
                    // Bare name or OBJECT::name → object scope (class 1).
                    permClass = PermissionChecker.ClassObject;
                    objectSecurableName = securableName;
                    break;
            }
        }

        // TO <principal_list> for GRANT / DENY, FROM <principal_list> for
        // REVOKE. Real SQL Server accepts TO for REVOKE too (probe-confirmed).
        if (context.Token is not ReservedKeyword { Keyword: Keyword.To or Keyword.From })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        var granteeNames = new List<string>();
        while (true)
        {
            var granteeName = context.Token switch
            {
                Name n => n.Value,
                ReservedKeyword rk => rk.ToString(),
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            granteeNames.Add(granteeName);
            context.MoveNextOptional();
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }

        // Optional trailers: WITH GRANT OPTION (GRANT only) / CASCADE
        // (REVOKE only) / AS grantor.
        var withGrantOption = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Grant })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Option })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            withGrantOption = true;
            context.MoveNextOptional();
        }
        var cascade = false;
        if (kind == PermissionStatementKind.Revoke
            && (context.Token is ReservedKeyword { Keyword: Keyword.Cascade }
                || (context.Token is UnquotedString { Value: var revokeTrailer } && revokeTrailer.Equals("CASCADE", StringComparison.OrdinalIgnoreCase))))
        {
            cascade = true;
            context.MoveNextOptional();
        }
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
        {
            context.MoveNextRequired();
            if (context.Token is not Name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            _ = BatchContext.ParseObjectName(context);
            context.MoveNextOptional();
        }

        if (context.Batch.IsSkipping)
            return true;

        // Server-scope GRANT / DENY / REVOKE: no ON clause and every permission
        // is a recognized server permission (CONNECT SQL, VIEW SERVER STATE, …).
        // Legal only in master (Msg 4621 elsewhere); stored at the Simulation
        // level and projected through sys.server_permissions.
        if (!hadOnClause && permissions.Count > 0 && permissions.TrueForAll(p => p.Columns is null && IsServerScopePermission(p.Name)))
        {
            ApplyServerScopeGrant(context, kind, permissions.ConvertAll(p => p.Name), granteeNames);
            return true;
        }

        // Fold a securable-placed column list (GRANT SELECT ON t (a, b)) into
        // every permission. It cannot combine with a per-permission list
        // (GRANT SELECT (a) ON t (b) is malformed).
        if (objectColumns is not null)
        {
            if (permissions.Exists(p => p.Columns is not null))
                throw SimulatedSqlException.GrantInvalidColumnListAfterObject();
            for (var i = 0; i < permissions.Count; i++)
                permissions[i] = (permissions[i].Name, objectColumns);
        }

        // The three object permissions with a column form. Every other
        // permission is entity-level, so a column list on it is Msg 1020.
        static bool PermissionAcceptsColumnList(string name) =>
            BuiltInToken.EqualsAny(name.Trim(), "SELECT", "UPDATE", "REFERENCES");

        // A parenthesized column list is legal only on an object-scope grant,
        // and then only for the three permissions that have a column form.
        // Everything else is entity-level and takes no sub-entity list —
        // probed across SELECT / UPDATE / REFERENCES (accepted) vs INSERT /
        // DELETE / EXECUTE / ALTER / CONTROL / TAKE OWNERSHIP / VIEW DEFINITION
        // / VIEW CHANGE TRACKING / RECEIVE (all Msg 1020).
        // Real reports this at Class 15 — a compile-time rejection that fires
        // before the securable resolves, so it beats the Msg 4606 kind check
        // (GRANT EXECUTE (col) on a *table* is 1020, not 4606) and TRY/CATCH
        // can't intercept it. Hence its position ahead of the resolution chain.
        if (permissions.Exists(p => p.Columns is not null
            && (permClass != PermissionChecker.ClassObject || !PermissionAcceptsColumnList(p.Name))))
        {
            throw SimulatedSqlException.GrantSubEntityListNotAllowed();
        }

        var database = context.CurrentDatabase;
        SchemaObject? securableObject = null;

        // Resolve a USER::x securable to its target principal id (class 4).
        if (userSecurableName is { } targetName)
        {
            if (!database.Principals.TryGetValue(targetName.Leaf, out var targetPrincipal))
                throw SimulatedSqlException.CannotFindPrincipal(targetName.Leaf);
            permMajorId = targetPrincipal.PrincipalId;
        }
        // Resolve an object securable (bare / OBJECT:: / SCHEMA::) to (class,
        // major_id). An unknown securable raises the 15151 object-variant.
        else if (permClass == PermissionChecker.ClassSchema)
        {
            if (!database.Schemas.TryGetValue(objectSecurableName!.Value.Leaf, out var schema))
                throw SimulatedSqlException.CannotFindObject(objectSecurableName.Value.Leaf);
            permMajorId = schema.SchemaId;
        }
        else if (permClass == PermissionChecker.ClassObject)
        {
            if (!TryResolveSecurableObject(context.Batch, objectSecurableName!.Value, out var obj))
                throw SimulatedSqlException.CannotFindObject(objectSecurableName.Value.Leaf);
            securableObject = obj;
            permMajorId = obj.ObjectId;
            // A synonym is entity-level: real accepts SELECT / UPDATE /
            // REFERENCES on it but takes no column list, and reports that only
            // once the securable has resolved — severity 16 state 3, ahead of
            // the Msg 4615 unknown-column check.
            if (obj is Synonym && permissions.Exists(p => p.Columns is not null))
                throw SimulatedSqlException.GrantSubEntityListNotAllowedOnSynonym();
            foreach (var (permName, _) in permissions)
                ValidatePermissionAgainstObjectKind(permName, obj.ObjectTypeCode);
        }


        // Msg 4624: a grant / deny / revoke targeting sa / dbo / sys /
        // INFORMATION_SCHEMA / entity owner / self is a silent no-op delivered
        // on the info-message channel (not catchable by TRY/CATCH).
        var effectivePrincipalId = context.Connection.Security.Effective.DatabasePrincipalId;
        foreach (var granteeName in granteeNames)
        {
            if (IsProtectedGrantTarget(database, granteeName, effectivePrincipalId))
            {
                context.Batch.AppendInfoError(@class: 16, state: 1, number: 4624,
                    message: "Cannot grant, deny, or revoke permissions to sa, dbo, entity owner, information_schema, sys, or yourself.");
                return true;
            }
        }

        // Delegated-authority gate: a non-dbo session may only GRANT / DENY /
        // REVOKE a permission it holds WITH GRANT OPTION. Missing authority
        // surfaces the 15151 object-variant (permission errors leak as
        // "cannot find the object").
        if (!context.Connection.Security.EffectiveIsDbo)
        {
            foreach (var (permName, _) in permissions)
            {
                if (!HasGrantAuthority(database, effectivePrincipalId, permName, permClass, permMajorId))
                    throw SimulatedSqlException.CannotFindObject(securableDisplayName);
            }
        }

        foreach (var granteeName in granteeNames)
        {
            if (!database.Principals.TryGetValue(granteeName, out var grantee))
                throw SimulatedSqlException.CannotFindPrincipal(granteeName);
            foreach (var (permName, columns) in permissions)
            {
                if (columns is null)
                {
                    ApplyOnePermission(database, kind, revokeGrantOptionOnly, cascade, withGrantOption,
                        permClass, permMajorId, minorId: 0, permName, grantee.PrincipalId, effectivePrincipalId);
                    continue;
                }
                // Column-level grant: one row per named column, minor_id =
                // 1-based column ordinal (sys.columns.column_id).
                foreach (var columnName in columns)
                {
                    var minorId = ResolveColumnMinorId(securableObject, columnName);
                    ApplyOnePermission(database, kind, revokeGrantOptionOnly, cascade, withGrantOption,
                        permClass, permMajorId, minorId, permName, grantee.PrincipalId, effectivePrincipalId);
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Parses a parenthesized column list (<c>(a, b, c)</c>) following a
    /// permission name. On entry the cursor is on the opening <c>(</c>; on
    /// return it is on the first token after the closing <c>)</c>. Column names
    /// are captured raw (resolved to ordinals later, once the securable object
    /// is known).
    /// </summary>
    private static List<string> ParsePermissionColumnList(ParserContext context)
    {
        var columns = new List<string>();
        context.MoveNextRequired();
        while (true)
        {
            var columnName = context.Token switch
            {
                Name n => n.Value,
                ReservedKeyword rk => rk.ToString(),
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            columns.Add(columnName);
            context.MoveNextRequired();
            switch (context.Token)
            {
                case Operator { Character: ',' }:
                    context.MoveNextRequired();
                    continue;
                case Operator { Character: ')' }:
                    context.MoveNextOptional();
                    return columns;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }
    }

    /// <summary>
    /// Resolves a column name to its 1-based ordinal (<c>sys.columns.column_id</c>,
    /// the <c>minor_id</c> a column-level grant stores) on the securable object;
    /// raises Msg 4615 when the object has no such column. Tables and views are
    /// supported (the only column-bearing securables the grant grammar reaches).
    /// </summary>
    private static int ResolveColumnMinorId(SchemaObject? securableObject, string columnName)
    {
        var columns = securableObject switch
        {
            Storage.HeapTable table => table.Columns,
            View view => view.OutputColumns,
            _ => null,
        };
        if (columns is not null)
        {
            for (var i = 0; i < columns.Length; i++)
            {
                if (BuiltInToken.Comparer.Equals(columns[i].Name, columnName))
                    return i + 1;
            }
        }
        throw SimulatedSqlException.GrantInvalidColumnName(columnName);
    }

    /// <summary>
    /// Applies one (grantee, permission, securable) triple. GRANT stores a
    /// single G (or W with grant option) row, replacing any prior G/W;
    /// DENY stores a D row (coexisting with any G row); REVOKE removes the
    /// matching rows, honoring GRANT OPTION FOR (W→G downgrade) and CASCADE
    /// (subtree removal), and raising Msg 4611 when a grantable row has
    /// delegations but no CASCADE.
    /// </summary>
    private static void ApplyOnePermission(Database database, PermissionStatementKind kind, bool revokeGrantOptionOnly, bool cascade, bool withGrantOption,
        byte permClass, int permMajorId, int minorId, string permName, int granteeId, int grantorId)
    {
        var permEnum = Permission.Resolve(permName);
        // Off-catalog names carry their raw text on the row; canonical rows draw
        // name / type code from the catalog at projection time.
        var storedName = permEnum == Permission.Other ? permName : null;
        // A table-level (minor 0) apply matches every minor_id of the permission
        // for this grantee — so GRANT / REVOKE at object scope subsumes any prior
        // column-level rows (probe-confirmed); a column-level apply keys on its
        // own minor_id alone.
        bool MinorMatches(int rowMinor) => minorId == 0 || rowMinor == minorId;
        bool Matches(DatabasePermission p, PermissionState state) =>
            p.State == state
            && p.GranteePrincipalId == granteeId
            && MinorMatches(p.MinorId)
            && p.IsFor(permClass, permMajorId, permEnum, permName, database);

        switch (kind)
        {
            case PermissionStatementKind.Grant:
                // GRANT replaces any prior GRANT / GRANT-WITH-GRANT row for
                // this triple (a plain GRANT after a WITH GRANT OPTION
                // downgrades W→G). DENY rows are untouched.
                _ = database.Permissions.RemoveAll(p => Matches(p, PermissionState.Grant) || Matches(p, PermissionState.GrantWithGrantOption));
                database.Permissions.Add(new DatabasePermission(
                    permClass, permMajorId, minorId, granteePrincipalId: granteeId,
                    grantorPrincipalId: grantorId, permission: permEnum,
                    state: withGrantOption ? PermissionState.GrantWithGrantOption : PermissionState.Grant, permissionName: storedName));
                break;

            case PermissionStatementKind.Deny:
                // DENY replaces only a prior DENY row; G/W rows coexist with
                // the D row (the checker gives D precedence).
                _ = database.Permissions.RemoveAll(p => Matches(p, PermissionState.Deny));
                database.Permissions.Add(new DatabasePermission(
                    permClass, permMajorId, minorId, granteePrincipalId: granteeId,
                    grantorPrincipalId: grantorId, permission: permEnum, state: PermissionState.Deny, permissionName: storedName));
                break;

            case PermissionStatementKind.Revoke when revokeGrantOptionOnly:
                // REVOKE GRANT OPTION FOR: downgrade W→G. With CASCADE, also
                // remove the rows this grantee delegated. Without CASCADE, a
                // W row with delegations raises Msg 4611.
                var wRow = database.Permissions.Find(p => Matches(p, PermissionState.GrantWithGrantOption));
                if (wRow is null)
                    return;
                if (HasDelegations(database, granteeId, permClass, permMajorId, permName) && !cascade)
                    throw SimulatedSqlException.RevokeRequiresCascade();
                _ = database.Permissions.Remove(wRow);
                database.Permissions.Add(new DatabasePermission(
                    permClass, permMajorId, minorId, granteePrincipalId: granteeId,
                    grantorPrincipalId: grantorId, permission: permEnum, state: PermissionState.Grant, permissionName: storedName));
                if (cascade)
                    CascadeRemoveDelegations(database, granteeId, permClass, permMajorId, permName);
                break;

            default:
                // Plain REVOKE removes both G/W and D rows for the triple.
                var grantable = database.Permissions.Find(p => Matches(p, PermissionState.GrantWithGrantOption));
                if (grantable is not null && HasDelegations(database, granteeId, permClass, permMajorId, permName) && !cascade)
                    throw SimulatedSqlException.RevokeRequiresCascade();
                _ = database.Permissions.RemoveAll(p => Matches(p, PermissionState.Grant) || Matches(p, PermissionState.GrantWithGrantOption) || Matches(p, PermissionState.Deny));
                if (cascade)
                    CascadeRemoveDelegations(database, granteeId, permClass, permMajorId, permName);
                break;
        }
    }

    /// <summary>Whether <paramref name="granteeId"/> has delegated this permission to anyone (a row whose grantor is this grantee).</summary>
    private static bool HasDelegations(Database database, int granteeId, byte permClass, int permMajorId, string permName)
    {
        var permEnum = Permission.Resolve(permName);
        return database.Permissions.Exists(p =>
            p.GrantorPrincipalId == granteeId && p.IsFor(permClass, permMajorId, permEnum, permName, database));
    }

    /// <summary>
    /// Removes the whole delegation subtree rooted at <paramref name="granteeId"/>:
    /// every row this grantee granted for the permission, transitively.
    /// </summary>
    private static void CascadeRemoveDelegations(Database database, int granteeId, byte permClass, int permMajorId, string permName)
    {
        var permEnum = Permission.Resolve(permName);
        var frontier = new Queue<int>();
        frontier.Enqueue(granteeId);
        var visited = new HashSet<int> { granteeId };
        while (frontier.Count > 0)
        {
            var grantor = frontier.Dequeue();
            var delegated = database.Permissions.FindAll(p =>
                p.GrantorPrincipalId == grantor && p.IsFor(permClass, permMajorId, permEnum, permName, database));
            foreach (var row in delegated)
            {
                if (visited.Add(row.GranteePrincipalId))
                    frontier.Enqueue(row.GranteePrincipalId);
            }
            _ = database.Permissions.RemoveAll(p =>
                p.GrantorPrincipalId == grantor && p.IsFor(permClass, permMajorId, permEnum, permName, database));
        }
    }

    /// <summary>
    /// Whether a non-dbo grantor holds a WITH GRANT OPTION (W) row that
    /// authorizes granting <paramref name="permName"/> on the securable. A W row
    /// on the <em>same</em> securable for the requested permission or any
    /// permission that covers it authorizes (CONTROL-W on the object may GRANT
    /// SELECT on it — probe M9). A <em>wider-scope</em> W row does NOT (schema
    /// SELECT-W does not authorize an object-scope grant — probe M9b), so the
    /// covering walk stays within (<paramref name="permClass"/>,
    /// <paramref name="permMajorId"/>).
    /// </summary>
    private static bool HasGrantAuthority(Database database, int grantorId, string permName, byte permClass, int permMajorId)
    {
        var requested = Permission.Resolve(permName);
        foreach (var row in database.Permissions)
        {
            if (row.State != PermissionState.GrantWithGrantOption || row.GranteePrincipalId != grantorId
                || row.Class != permClass || row.MajorId != permMajorId)
            {
                continue;
            }
            // Off-catalog names match only their own stored text; catalog names
            // additionally match any covering permission on the same securable.
            if (requested == Permission.Other)
            {
                if (row.IsFor(permClass, permMajorId, requested, permName, database))
                    return true;
                continue;
            }
            Permission? current = requested;
            while (current is Permission p)
            {
                if (row.Permission == p)
                    return true;
                current = p.Covering(permClass);
            }
        }
        return false;
    }

    /// <summary>Whether a GRANT / DENY / REVOKE targeting <paramref name="granteeName"/> must silently no-op with Msg 4624 (sa / dbo / sys / INFORMATION_SCHEMA / self).</summary>
    private static bool IsProtectedGrantTarget(Database database, string granteeName, int effectivePrincipalId)
    {
        return BuiltInToken.Comparer.Equals(granteeName, "dbo")
            || BuiltInToken.Comparer.Equals(granteeName, "sa")
            || BuiltInToken.Comparer.Equals(granteeName, "sys")
            || BuiltInToken.Comparer.Equals(granteeName, "INFORMATION_SCHEMA")
            || (database.Principals.TryGetValue(granteeName, out var grantee) && grantee.PrincipalId == effectivePrincipalId);
    }

    /// <summary>Resolves a bare / OBJECT:: securable name to the schema object it names (table, view, function, procedure, sequence).</summary>
    private static bool TryResolveSecurableObject(BatchContext batch, MultiPartName name, out SchemaObject resolved)
    {
        if (batch.TryResolveSchema(name, out var schema))
        {
            foreach (var obj in schema.SchemaObjects())
            {
                if (BuiltInToken.Comparer.Equals(obj.Name, name.Leaf))
                {
                    resolved = obj;
                    return true;
                }
            }
        }
        resolved = null!;
        return false;
    }

    /// <summary>Raises Msg 4606 when a DML permission targets a proc / scalar-function, or EXECUTE targets a table / view / TVF / sequence.</summary>
    private static void ValidatePermissionAgainstObjectKind(string permName, string objectTypeCode)
    {
        // A synonym takes either family: real accepts GRANT SELECT and GRANT
        // EXECUTE on the same synonym (probe-confirmed), since the base object's
        // kind isn't consulted at grant time.
        var kindIsExecutable = objectTypeCode is "P " or "FN" or "PC" or "FS" or "FT" or "SN";
        var kindIsTabular = objectTypeCode is "U " or "V " or "IF" or "TF" or "SN";
        var isDml = permName.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            || permName.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
            || permName.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
            || permName.Equals("DELETE", StringComparison.OrdinalIgnoreCase);
        var isExecute = permName.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase);
        if (isDml && !kindIsTabular)
            throw SimulatedSqlException.PermissionIncompatibleWithObject(permName.ToUpperInvariant());
        if (isExecute && !kindIsExecutable)
            throw SimulatedSqlException.PermissionIncompatibleWithObject(permName.ToUpperInvariant());
    }

    /// <summary>
    /// Returns the bare identifier text for the current token if it's part of
    /// a permission name. Returns null at a clause boundary (comma / ON / TO /
    /// FROM / AS / WITH / semicolon / EOF).
    /// </summary>
    private static string? TryConsumePermissionWord(ParserContext context) => context.Token switch
    {
        ReservedKeyword { Keyword: Keyword.To or Keyword.From or Keyword.On or Keyword.As or Keyword.With } => null,
        Operator { Character: ',' or ';' or '(' or ')' } => null,
        null => null,
        ReservedKeyword rk => rk.ToString(),
        Name n => n.Value,
        _ => null,
    };

}

/// <summary>
/// Discriminates the three permission statement keywords for shared parsing
/// in <see cref="Simulation.TryParseGrantRevokeDeny"/>.
/// </summary>
internal enum PermissionStatementKind : byte
{
    Grant,
    Revoke,
    Deny,
}
