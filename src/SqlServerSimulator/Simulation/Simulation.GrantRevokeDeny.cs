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

        // Permission list — comma-separated spelled-out permission names.
        // Each permission is a sequence of one or more bare identifier tokens;
        // the sequence ends at a comma, ON, TO, FROM, or AS.
        var permissions = new List<string>();
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
                permissions.Add(string.Join(" ", currentTokens));
                currentTokens.Clear();
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
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
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

        var database = context.CurrentDatabase;

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
            permMajorId = obj.ObjectId;
            foreach (var permName in permissions)
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
            foreach (var permName in permissions)
            {
                if (!HasGrantAuthority(database, effectivePrincipalId, permName, permClass, permMajorId))
                    throw SimulatedSqlException.CannotFindObject(securableDisplayName);
            }
        }

        foreach (var granteeName in granteeNames)
        {
            if (!database.Principals.TryGetValue(granteeName, out var grantee))
                throw SimulatedSqlException.CannotFindPrincipal(granteeName);
            foreach (var permName in permissions)
            {
                ApplyOnePermission(database, kind, revokeGrantOptionOnly, cascade, withGrantOption,
                    permClass, permMajorId, permName, grantee.PrincipalId, effectivePrincipalId);
            }
        }
        return true;
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
        byte permClass, int permMajorId, string permName, int granteeId, int grantorId)
    {
        var permEnum = Permission.Resolve(permName);
        // Off-catalog names carry their raw text on the row; canonical rows draw
        // name / type code from the catalog at projection time.
        var storedName = permEnum == Permission.Other ? permName : null;
        bool Matches(DatabasePermission p, PermissionState state) =>
            p.State == state
            && p.GranteePrincipalId == granteeId
            && p.IsFor(permClass, permMajorId, permEnum, permName, database);

        switch (kind)
        {
            case PermissionStatementKind.Grant:
                // GRANT replaces any prior GRANT / GRANT-WITH-GRANT row for
                // this triple (a plain GRANT after a WITH GRANT OPTION
                // downgrades W→G). DENY rows are untouched.
                _ = database.Permissions.RemoveAll(p => Matches(p, PermissionState.Grant) || Matches(p, PermissionState.GrantWithGrantOption));
                database.Permissions.Add(new DatabasePermission(
                    permClass, permMajorId, minorId: 0, granteePrincipalId: granteeId,
                    grantorPrincipalId: grantorId, permission: permEnum,
                    state: withGrantOption ? PermissionState.GrantWithGrantOption : PermissionState.Grant, permissionName: storedName));
                break;

            case PermissionStatementKind.Deny:
                // DENY replaces only a prior DENY row; G/W rows coexist with
                // the D row (the checker gives D precedence).
                _ = database.Permissions.RemoveAll(p => Matches(p, PermissionState.Deny));
                database.Permissions.Add(new DatabasePermission(
                    permClass, permMajorId, minorId: 0, granteePrincipalId: granteeId,
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
                    permClass, permMajorId, minorId: 0, granteePrincipalId: granteeId,
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

    /// <summary>Whether a non-dbo grantor holds a WITH GRANT OPTION (W) row that authorizes granting <paramref name="permName"/> on the securable (directly or via a covering W row at a wider scope).</summary>
    private static bool HasGrantAuthority(Database database, int grantorId, string permName, byte permClass, int permMajorId)
    {
        // A W row for the exact permission on the exact securable is the common
        // case; the checker's covering/scope walk generalizes it (a W CONTROL
        // or a wider-scope W row also authorizes delegation).
        var permEnum = Permission.Resolve(permName);
        foreach (var row in database.Permissions)
        {
            if (row.State == PermissionState.GrantWithGrantOption && row.GranteePrincipalId == grantorId
                && row.IsFor(permClass, permMajorId, permEnum, permName, database))
            {
                return true;
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
        var kindIsExecutable = objectTypeCode is "P " or "FN" or "PC" or "FS" or "FT";
        var kindIsTabular = objectTypeCode is "U " or "V " or "IF" or "TF";
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
