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
        var principalClassWord = "user";
        MultiPartName? objectSecurableName = null;
        List<string>? objectColumns = null;
        var hadOnClause = false;
        // ON SERVER::x (the name is ignored — probe-confirmed real accepts any
        // name there and stores the ordinary class-100 row) and ON LOGIN::x
        // (class 101) both route to the Simulation-level server registry.
        var serverScopeSecurable = false;
        string? loginSecurableName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            hadOnClause = true;
            context.MoveNextRequired();
            // One to three class words and `::`, or none (an object).
            var explicitClass = ParseAuthorizationClass(context);
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
                    // Another database is Msg 4610, one that isn't there Msg
                    // 15151 (probed 2026-10-04 against SQL Server 2025).
                    if (!context.Batch.IsSkipping && !context.CurrentDatabase.Collation.Equals(securableName.Leaf, context.CurrentDatabase.Name))
                    {
                        throw context.Connection.Simulation.Databases.ContainsKey(securableName.Leaf)
                            ? SimulatedSqlException.GrantOnAnotherDatabase()
                            : SimulatedSqlException.CannotFindSecurable("database", securableName.Leaf);
                    }
                    break;
                case "FULLTEXT CATALOG":
                    permClass = PermissionChecker.ClassFulltextCatalog;
                    objectSecurableName = securableName;
                    break;
                case "LOGIN":
                    serverScopeSecurable = true;
                    loginSecurableName = securableName.Leaf;
                    break;
                case "ROLE":
                    // A role is a database principal like a user: class 4
                    // DATABASE_PRINCIPAL.
                    permClass = PermissionChecker.ClassDatabasePrincipal;
                    userSecurableName = securableName;
                    principalClassWord = "role";
                    break;
                case "SCHEMA":
                    permClass = PermissionChecker.ClassSchema;
                    objectSecurableName = securableName;
                    break;
                case "SERVER":
                    serverScopeSecurable = true;
                    break;
                case "TYPE":
                    permClass = PermissionChecker.ClassType;
                    objectSecurableName = securableName;
                    break;
                case "USER":
                    permClass = PermissionChecker.ClassDatabasePrincipal;
                    userSecurableName = securableName;
                    break;
                case "XML SCHEMA COLLECTION":
                    permClass = PermissionChecker.ClassXmlSchemaCollection;
                    objectSecurableName = securableName;
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
        // GRANT and DENY take TO alone; REVOKE either (probed 2026-10-04
        // against SQL Server 2025: DENY … FROM is Msg 102 at line 0).
        if (context.Token is ReservedKeyword { Keyword: Keyword.From } && kind != PermissionStatementKind.Revoke)
            throw SimulatedSqlException.SyntaxErrorNearText("from").PinLine(0);
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
        if (kind != PermissionStatementKind.Grant
            && (context.Token is ReservedKeyword { Keyword: Keyword.Cascade }
                || (context.Token is UnquotedString { Value: var revokeTrailer } && revokeTrailer.Equals("CASCADE", StringComparison.OrdinalIgnoreCase))))
        {
            cascade = true;
            context.MoveNextOptional();
        }
        string? asGrantor = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
        {
            context.MoveNextRequired();
            if (context.Token is not Name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            asGrantor = BatchContext.ParseObjectName(context).Leaf;
            context.MoveNextOptional();
        }

        if (context.Batch.IsSkipping)
            return true;

        // A permission row is a log write whatever its scope, and the refusal
        // precedes every resolution below — a GRANT naming a missing object or
        // a missing principal still reports Msg 3930 (probe-confirmed).
        RejectWriteInDoomedTransaction(context.Connection);

        // Server-scope GRANT / DENY / REVOKE. Three routes in: no ON clause with
        // every permission a recognized server permission (CONNECT SQL, VIEW
        // SERVER STATE, …), an explicit ON SERVER::x, or an ON LOGIN::x (class
        // 101, whose permissions are the ordinary catalog names). Legal only in
        // master (Msg 4621 elsewhere); stored at the Simulation level and
        // projected through sys.server_permissions.
        if (serverScopeSecurable
            || (!hadOnClause && permissions.Count > 0 && permissions.TrueForAll(p => p.Columns is null && IsServerScopePermission(p.Name))))
        {
            if (permissions.Exists(p => p.Columns is not null))
                throw SimulatedSqlException.GrantSubEntityListNotAllowed();
            ApplyServerScopeGrant(context, kind, permissions.ConvertAll(p => p.Name), granteeNames, loginSecurableName);
            return true;
        }

        // Fold a securable-placed column list (GRANT SELECT ON t (a, b)) into
        // every permission. It cannot combine with a per-permission list
        // (GRANT SELECT (a) ON t (b) is malformed).
        if (objectColumns is not null)
        {
            if (permissions.Exists(p => p.Columns is not null))
                throw SimulatedSqlException.GrantInvalidColumnListAfterObject().PinLine(0);
            for (var i = 0; i < permissions.Count; i++)
                permissions[i] = (permissions[i].Name, objectColumns);
        }

        // The object permissions with a column form — UNMASK's names the masked
        // columns it reveals (probed 2026-09-27). Every other permission is
        // entity-level, so a column list on it is Msg 1020.
        static bool PermissionAcceptsColumnList(string name) =>
            BuiltInToken.EqualsAny(name.Trim(), "SELECT", "UPDATE", "REFERENCES", "UNMASK");

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
            throw SimulatedSqlException.GrantSubEntityListNotAllowed().PinLine(0);
        }

        // The permissions a type, an XML schema collection and a full-text
        // catalog carry; any other is a syntax error at compile, at line 0
        // (probed 2026-09-29 against SQL Server 2025).
        if (permClass is PermissionChecker.ClassType or PermissionChecker.ClassXmlSchemaCollection or PermissionChecker.ClassFulltextCatalog)
        {
            foreach (var (permName, _) in permissions)
            {
                if (!PermissionAcceptedOnDerivedClass(permClass, permName))
                    throw SimulatedSqlException.SyntaxErrorNearText(permName.ToUpperInvariant()).PinLine(0);
            }
        }

        var database = context.CurrentDatabase;
        // A database-scope permission row is a write to that database's
        // catalog, so a read-only database refuses the whole family (the
        // server-scope branch above wrote to master, which can never be
        // read-only).
        database.RejectWriteWhenReadOnly();
        SchemaObject? securableObject = null;
        DatabasePrincipal? securablePrincipal = null;

        // Resolve a USER::x / ROLE::x securable to its target principal id
        // (class 4). Each class finds only its own kind — an application role
        // or a fixed role reads as no object at all (probed 2026-10-04
        // against SQL Server 2025).
        if (userSecurableName is { } targetName)
        {
            if (!database.Principals.TryGetValue(targetName.Leaf, out var targetPrincipal))
                throw SimulatedSqlException.CannotFindSecurable(principalClassWord, targetName.Leaf);
            if (targetPrincipal.TypeCode == "A" || targetPrincipal.IsFixedRole || principalClassWord == "role" != (targetPrincipal.TypeCode == "R"))
                throw SimulatedSqlException.CannotFindObject(targetName.Leaf);
            permMajorId = targetPrincipal.PrincipalId;
            securablePrincipal = targetPrincipal;
        }
        else if (permClass is PermissionChecker.ClassType or PermissionChecker.ClassXmlSchemaCollection or PermissionChecker.ClassFulltextCatalog)
        {
            permMajorId = ResolveDerivedSecurable(context, database, permClass, objectSecurableName!.Value);
        }
        // Resolve an object securable (bare / OBJECT:: / SCHEMA::) to (class,
        // major_id). An unknown securable raises the 15151 object-variant.
        else if (permClass == PermissionChecker.ClassSchema)
        {
            if (!database.Schemas.TryGetValue(objectSecurableName!.Value.Leaf, out var schema))
                throw SimulatedSqlException.CannotFindSecurable("schema", objectSecurableName.Value.Leaf);
            permMajorId = schema.SchemaId;
        }
        else if (permClass == PermissionChecker.ClassObject
            && objectSecurableName!.Value.Count >= 3 && objectSecurableName.Value[objectSecurableName.Value.Count - 3] is { Length: > 0 } databasePart
            && !database.Collation.Equals(databasePart, database.Name))
        {
            throw SimulatedSqlException.GrantOnAnotherDatabase();
        }
        else if (permClass == PermissionChecker.ClassObject
            && !TryResolveSecurableObject(context.Batch, objectSecurableName!.Value, out _)
            && context.Batch.TryResolveCatalogView(objectSecurableName.Value, out var catalogView, out var catalogDatabase)
            && ReferenceEquals(catalogDatabase, database))
        {
            // A catalog view is a securable of the database it's read in, as
            // the SELECT every database grants public on its system views is
            // (probed 2026-09-28 against SQL Server 2025) — except the
            // INFORMATION_SCHEMA views, which only master grants on (Msg 4629).
            if (BuiltInResources.CatalogViewsById.Value.TryGetValue(catalogView.ObjectId, out var catalogEntry)
                && catalogEntry.SchemaName == "INFORMATION_SCHEMA" && !BuiltInToken.Equals(database.Name, MasterDatabaseName))
            {
                throw SimulatedSqlException.GrantOnServerScopedObjectOutsideMaster();
            }
            if (permissions.Exists(p => p.Columns is not null))
                throw new NotSupportedException("A column-level permission on a catalog view is not supported.");
            permMajorId = catalogView.ObjectId;
            foreach (var (permName, _) in permissions)
                ValidatePermissionAgainstObjectKind(permName, "V ");
        }
        else if (permClass == PermissionChecker.ClassObject)
        {
            if (!TryResolveSecurableObject(context.Batch, objectSecurableName!.Value, out var obj))
            {
                // A system procedure is granted on only in master (probed
                // 2026-10-04 against SQL Server 2025).
                if (BuiltInToken.Equals(objectSecurableName.Value.ImmediateQualifier ?? "", "sys") && objectSecurableName.Value.Leaf.StartsWith("sp_", StringComparison.OrdinalIgnoreCase)
                    && !BuiltInToken.Equals(database.Name, MasterDatabaseName))
                {
                    throw SimulatedSqlException.GrantOnServerScopedObjectOutsideMaster();
                }
                throw SimulatedSqlException.CannotFindObject(objectSecurableName.Value.Leaf);
            }
            securableObject = obj;
            permMajorId = obj.ObjectId;
            // A synonym is entity-level: real accepts SELECT / UPDATE /
            // REFERENCES on it but takes no column list, and reports that only
            // once the securable has resolved — severity 16 state 3, ahead of
            // the Msg 4615 unknown-column check.
            if (obj is Synonym && permissions.Exists(p => p.Columns is not null))
                throw SimulatedSqlException.GrantSubEntityListNotAllowedOnSynonym();
            permissions = ExpandAll(context, permissions, obj.ObjectTypeCode);
            foreach (var (permName, _) in permissions)
            {
                if (!PermissionGraph.IsPermissionOf("OBJECT", CanonicalPermissionName(permName)))
                    throw SimulatedSqlException.SyntaxErrorNearText(CanonicalPermissionName(permName)).PinLine(0);
                ValidatePermissionAgainstObjectKind(permName, obj.ObjectTypeCode);
            }
        }

        // The permission names a class carries; any other is Msg 102 near the
        // name at line 0 (probed 2026-10-04 against SQL Server 2025: CREATE
        // TABLE on a schema, CONNECT on a table, CREATE SEQUENCE on the
        // database, which is a schema permission).
        if (permClass == PermissionChecker.ClassDatabase)
            permissions = ExpandAll(context, permissions, objectTypeCode: null);
        if (permClass is PermissionChecker.ClassDatabase or PermissionChecker.ClassSchema or PermissionChecker.ClassDatabasePrincipal)
        {
            var classDescription = permClass switch
            {
                PermissionChecker.ClassDatabase => "DATABASE",
                PermissionChecker.ClassSchema => "SCHEMA",
                _ => securablePrincipal!.TypeCode == "R" ? "ROLE" : "USER",
            };
            foreach (var (permName, _) in permissions)
            {
                if (!PermissionGraph.IsPermissionOf(classDescription, CanonicalPermissionName(permName)))
                    throw SimulatedSqlException.SyntaxErrorNearText(CanonicalPermissionName(permName)).PinLine(0);
            }
        }

        // Every grantee resolves before any row changes, so a missing one
        // leaves the statement's other grantees untouched; a fixed database
        // role takes no permissions (Msg 4617); and the protected principals
        // and the securable's owner turn the statement into Msg 4624 on the
        // info channel — state 2 for sa / dbo / sys / INFORMATION_SCHEMA /
        // self, state 3 for the owner (probed 2026-10-04 against SQL Server
        // 2025).
        var effectivePrincipalId = context.Connection.Security.Effective.DatabasePrincipalId;
        var securableOwnerId = SecurableOwnerId(database, permClass, permMajorId, securableObject, securablePrincipal);
        var grantees = new List<DatabasePrincipal>(granteeNames.Count);
        foreach (var granteeName in granteeNames)
        {
            if (IsProtectedGrantTarget(database, granteeName, effectivePrincipalId))
            {
                context.Batch.AppendInfoError(@class: 0, state: 2, number: 4624,
                    message: "Cannot grant, deny, or revoke permissions to sa, dbo, entity owner, information_schema, sys, or yourself.");
                return true;
            }
            if (!database.Principals.TryGetValue(granteeName, out var grantee))
                throw SimulatedSqlException.CannotFindUser(granteeName);
            if (grantee.IsFixedRole && grantee.PrincipalId != Database.PublicPrincipalId)
                throw SimulatedSqlException.GrantToSpecialRole();
            if (grantee.PrincipalId == securableOwnerId)
            {
                // A user granted a permission on itself is a silent no-op
                // (probed 2026-10-06 against SQL Server 2025).
                if (permClass == PermissionChecker.ClassDatabasePrincipal && securablePrincipal is { TypeCode: not "R" })
                    return true;
                context.Batch.AppendInfoError(@class: 0, state: 3, number: 4624,
                    message: "Cannot grant, deny, or revoke permissions to sa, dbo, entity owner, information_schema, sys, or yourself.");
                return true;
            }

            // master and tempdb keep guest's seeded CONNECT.
            if (kind != PermissionStatementKind.Grant
                && permClass == PermissionChecker.ClassDatabase
                && BuiltInToken.Equals(granteeName, "guest")
                && BuiltInToken.EqualsAny(database.Name, MasterDatabaseName, TempdbDatabaseName)
                && permissions.Exists(p => Permission.Resolve(p.Name) == Permission.Connect))
            {
                throw SimulatedSqlException.CannotDisableGuestAccess();
            }
            grantees.Add(grantee);
        }

        // The grantor. AS names a principal the session is, belongs to or
        // may impersonate (else Msg 15151 naming it as a user). Its authority
        // is CONTROL of the securable, db_securityadmin membership, or for
        // CONNECT db_accessadmin membership, recorded under the securable's
        // owner — so dbo's grants on a user's object name that user — or else
        // a grant option on the securable itself, recorded under its own name. DENY takes the second kind alone. Missing authority reads as
        // the securable not being there, Msg 4613 for the database (probed
        // 2026-10-04 against SQL Server 2025).
        var grantorId = effectivePrincipalId;
        if (asGrantor is not null)
        {
            var closure = PermissionChecker.BuildPrincipalClosure(database, effectivePrincipalId);
            if (!database.Principals.TryGetValue(asGrantor, out var asPrincipal)
                || !(context.Connection.Security.EffectiveIsDbo
                    || closure.Contains(asPrincipal.PrincipalId)
                    || closure.Contains(DbSecurityAdminRoleId)
                    || PermissionChecker.IsGranted(database, effectivePrincipalId, Permission.Impersonate, PermissionChecker.ClassDatabasePrincipal, asPrincipal.PrincipalId, 0)))
            {
                throw SimulatedSqlException.CannotFindUser(asGrantor);
            }
            grantorId = asPrincipal.PrincipalId;
        }
        var recordedGrantor = grantorId;
        if (grantorId == Database.DboPrincipalId)
        {
            recordedGrantor = securableOwnerId;
        }
        else
        {
            var schemaId = securableObject?.SchemaId ?? 0;
            foreach (var (permName, _) in permissions)
            {
                if (HoldsOwnerAuthority(database, grantorId, permName, permClass, permMajorId, schemaId, ServerLoginRights.For(context.Connection)))
                {
                    recordedGrantor = securableOwnerId;
                    continue;
                }
                if (kind != PermissionStatementKind.Deny && HasGrantAuthority(database, grantorId, permName, permClass, permMajorId))
                {
                    recordedGrantor = grantorId;
                    continue;
                }
                throw permClass switch
                {
                    PermissionChecker.ClassDatabase => SimulatedSqlException.GrantorLacksGrantPermission(),
                    PermissionChecker.ClassSchema => SimulatedSqlException.CannotFindSecurable("schema", securableDisplayName),
                    _ => SimulatedSqlException.CannotFindObject(securableDisplayName),
                };
            }
        }

        // DENY of a permission a grantee holds with the grant option needs
        // CASCADE, as REVOKE does (Msg 4611).
        if (kind == PermissionStatementKind.Deny && !cascade)
        {
            foreach (var grantee in grantees)
            {
                foreach (var (permName, _) in permissions)
                {
                    var permEnum = Permission.Resolve(permName);
                    if (database.Permissions.Exists(p => p.State == PermissionState.GrantWithGrantOption && p.GranteePrincipalId == grantee.PrincipalId
                        && p.IsFor(permClass, permMajorId, permEnum, CanonicalPermissionName(permName), database)))
                    {
                        throw SimulatedSqlException.RevokeRequiresCascade();
                    }
                }
            }
        }

        // Read ahead of the rows changing: the event's CascadeOption reports a
        // CASCADE that had a grant option to reach.
        (string EventType, string SchemaName, string ObjectName, string ObjectType, string TrailingElements)? permissionEvent = RaisesDdlEvents(context)
            ? RenderPermissionEvent(context, database, kind, permissions, granteeNames, permClass, permMajorId, securableObject, objectSecurableName,
                revokeGrantOptionOnly, withGrantOption, cascade, asGrantor)
            : null;
        RecordSecurityUndo(context, database);
        foreach (var grantee in grantees)
        {
            foreach (var (permName, columns) in permissions)
            {
                if (columns is null)
                {
                    ApplyOnePermission(database, kind, revokeGrantOptionOnly, cascade, withGrantOption,
                        permClass, permMajorId, minorId: 0, CanonicalPermissionName(permName), grantee.PrincipalId, recordedGrantor);
                    continue;
                }
                // Column-level grant: one row per named column, minor_id =
                // 1-based column ordinal (sys.columns.column_id).
                foreach (var columnName in columns)
                {
                    var minorId = ResolveColumnMinorId(securableObject, columnName);
                    ApplyOnePermission(database, kind, revokeGrantOptionOnly, cascade, withGrantOption,
                        permClass, permMajorId, minorId, CanonicalPermissionName(permName), grantee.PrincipalId, recordedGrantor);
                }
            }
        }
        if (permissionEvent is { } recorded)
        {
            RecordDdlEvent(context, recorded.EventType, recorded.SchemaName, recorded.ObjectName, recorded.ObjectType,
                trailingElements: recorded.TrailingElements);
        }
        return true;
    }

    /// <summary>
    /// The <c>GRANT_DATABASE</c> / <c>DENY_DATABASE</c> / <c>REVOKE_DATABASE</c>
    /// event a database-scope permission statement raises, one per statement
    /// (probed 2026-09-28 against SQL Server 2025): the securable — an object
    /// under its schema, a schema, principal or the database itself under an
    /// empty <c>SchemaName</c> — then the grantor (a user or application role
    /// securable itself, a role securable its owner), the permissions in lower case without their column
    /// lists, the grantees, the <c>AS</c> principal, and the two options, a
    /// <c>CASCADE</c> counting only when a grantee held the grant option it
    /// reaches.
    /// </summary>
    private static (string EventType, string SchemaName, string ObjectName, string ObjectType, string TrailingElements) RenderPermissionEvent(
        ParserContext context, Database database, PermissionStatementKind kind, List<(string Name, List<string>? Columns)> permissions,
        List<string> granteeNames, byte permClass, int permMajorId, SchemaObject? securableObject, MultiPartName? objectSecurableName,
        bool revokeGrantOptionOnly, bool withGrantOption, bool cascade, string? asGrantor)
    {
        var grantor = context.Connection.Security.Effective.DatabasePrincipalName;
        var (schemaName, objectName, objectType) = ("", database.Name, "DATABASE");
        if (securableObject is not null)
        {
            (schemaName, objectName, objectType) = (EventSchemaName(objectSecurableName!.Value), securableObject.Name, AuthorizationObjectType(securableObject));
        }
        else if (permClass == PermissionChecker.ClassSchema)
        {
            objectName = objectSecurableName!.Value.Leaf;
            objectType = "SCHEMA";
        }
        else if (permClass is PermissionChecker.ClassType or PermissionChecker.ClassXmlSchemaCollection or PermissionChecker.ClassFulltextCatalog)
        {
            // Unprobed: the object types are the ones ALTER AUTHORIZATION reports for the same classes.
            objectName = objectSecurableName!.Value.Leaf;
            objectType = permClass switch
            {
                PermissionChecker.ClassType => "TYPE",
                PermissionChecker.ClassXmlSchemaCollection => "XML SCHEMA COLLECTION",
                _ => "FULLTEXT CATALOG",
            };
            if (permClass != PermissionChecker.ClassFulltextCatalog)
                schemaName = EventSchemaName(objectSecurableName.Value);
        }
        else if (permClass == PermissionChecker.ClassDatabasePrincipal)
        {
            foreach (var (_, principal) in database.Principals)
            {
                if (principal.PrincipalId != permMajorId)
                    continue;
                objectName = principal.Name;
                objectType = principal.TypeCode switch
                {
                    "A" => "APPLICATION ROLE",
                    "R" => "ROLE",
                    _ => "USER",
                };
                // A role reports its owner as the grantor, any other principal
                // itself.
                grantor = principal.TypeCode == "R" ? Ownership.PrincipalName(database, principal.OwningPrincipalId) ?? principal.Name : principal.Name;
            }
        }

        var cascadeReaches = false;
        if (cascade)
        {
            foreach (var permission in database.Permissions)
            {
                if (permission.State != PermissionState.GrantWithGrantOption)
                    continue;
                foreach (var granteeName in granteeNames)
                {
                    if (database.Principals.TryGetValue(granteeName, out var grantee) && grantee.PrincipalId == permission.GranteePrincipalId
                        && permissions.Exists(p => permission.IsFor(permClass, permMajorId, Permission.Resolve(p.Name), p.Name, database)))
                    {
                        cascadeReaches = true;
                    }
                }
            }
        }

        var elements = new System.Text.StringBuilder();
        AppendElement(elements, "Grantor", grantor);
        _ = elements.Append("<Permissions>");
        foreach (var (name, _) in permissions)
        {
#pragma warning disable CA1308 // Real reports the permission names in lower case.
            AppendElement(elements, "Permission", name.ToLowerInvariant());
#pragma warning restore CA1308
        }
        _ = elements.Append("</Permissions><Grantees>");
        foreach (var granteeName in granteeNames)
            AppendElement(elements, "Grantee", granteeName);
        _ = elements.Append("</Grantees>");
        AppendElement(elements, "AsGrantor", asGrantor ?? "");
        AppendElement(elements, "GrantOption", withGrantOption || revokeGrantOptionOnly ? "1" : "0");
        AppendElement(elements, "CascadeOption", cascadeReaches ? "1" : "0");
        var eventType = kind switch
        {
            PermissionStatementKind.Grant => "GRANT_DATABASE",
            PermissionStatementKind.Deny => "DENY_DATABASE",
            _ => "REVOKE_DATABASE",
        };
        return (eventType, schemaName, objectName, objectType, elements.ToString());
    }

    /// <summary>The canonical spelling a permission name is stored and validated under: upper case, single-spaced, <c>EXEC</c> as <c>EXECUTE</c>.</summary>
    private static string CanonicalPermissionName(string permName)
    {
        var upper = string.Join(' ', permName.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return upper == "EXEC" ? "EXECUTE" : upper;
    }

    /// <summary>
    /// Expands the deprecated <c>ALL</c> (or <c>ALL PRIVILEGES</c>) into the
    /// permissions it stands for on the securable's kind, delivering Msg 4628
    /// on the info channel (probed 2026-10-04 against SQL Server 2025: a
    /// table's DELETE, INSERT, REFERENCES, SELECT and UPDATE, a procedure's
    /// EXECUTE, the database's eight statement permissions).
    /// </summary>
    private static List<(string Name, List<string>? Columns)> ExpandAll(ParserContext context, List<(string Name, List<string>? Columns)> permissions, string? objectTypeCode)
    {
        if (!permissions.Exists(p => CanonicalPermissionName(p.Name) is "ALL" or "ALL PRIVILEGES"))
            return permissions;
        context.Batch.AppendInfoError(@class: 0, state: 2, number: 4628,
            message: "The ALL permission is deprecated and maintained only for compatibility. It DOES NOT imply ALL permissions defined on the entity.");
        string[] implied = objectTypeCode switch
        {
            null => ["BACKUP DATABASE", "BACKUP LOG", "CREATE DEFAULT", "CREATE FUNCTION", "CREATE PROCEDURE", "CREATE RULE", "CREATE TABLE", "CREATE VIEW"],
            "FN" or "FS" => ["EXECUTE", "REFERENCES"],
            "P " or "PC" or "X " => ["EXECUTE"],
            "SO" => ["REFERENCES", "UPDATE"],
            _ => ["DELETE", "INSERT", "REFERENCES", "SELECT", "UPDATE"],
        };
        var expanded = new List<(string Name, List<string>? Columns)>();
        foreach (var permission in permissions)
        {
            if (CanonicalPermissionName(permission.Name) is "ALL" or "ALL PRIVILEGES")
            {
                foreach (var name in implied)
                    expanded.Add((name, permission.Columns));
            }
            else
            {
                expanded.Add(permission);
            }
        }
        return expanded;
    }

    /// <summary>
    /// The principal that owns the securable a permission statement names —
    /// the grantor a row records when the authority behind it is ownership
    /// rather than a grant option (probed 2026-10-04 against SQL Server 2025:
    /// a user owns itself, a schema answers with its owner).
    /// </summary>
    private static int SecurableOwnerId(Database database, byte permClass, int permMajorId, SchemaObject? securableObject, DatabasePrincipal? securablePrincipal) => permClass switch
    {
        PermissionChecker.ClassObject when securableObject is not null => Ownership.EffectiveOwnerId(database, securableObject),
        PermissionChecker.ClassSchema => Ownership.SchemaOwnerId(database, permMajorId),
        PermissionChecker.ClassDatabasePrincipal when securablePrincipal is not null =>
            securablePrincipal.TypeCode == "R" ? securablePrincipal.OwningPrincipalId : securablePrincipal.PrincipalId,
        _ => Database.DboPrincipalId,
    };

    /// <summary>
    /// Whether <paramref name="grantorId"/> may grant, deny or revoke on the
    /// securable as its owner would: it holds <c>CONTROL</c> of it, it is a
    /// <c>db_securityadmin</c> member, or the permission is <c>CONNECT</c> and
    /// it is a <c>db_accessadmin</c> member (probed 2026-10-04 against SQL
    /// Server 2025).
    /// </summary>
    private static bool HoldsOwnerAuthority(Database database, int grantorId, string permName, byte permClass, int permMajorId, int schemaId, ServerLoginRights server)
    {
        var closure = PermissionChecker.BuildPrincipalClosure(database, grantorId);
        return closure.Contains(DbSecurityAdminRoleId)
            || (closure.Contains(DbAccessAdminRoleId) && permClass == PermissionChecker.ClassDatabase && CanonicalPermissionName(permName) == "CONNECT")
            || PermissionChecker.IsGranted(database, grantorId, Permission.Control, permClass, permMajorId, schemaId, server);
    }

    private const int DbAccessAdminRoleId = 16385;

    private const int DbSecurityAdminRoleId = 16386;

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
    /// (subtree removal), and raising Msg 4611 when a grantable row is revoked
    /// without CASCADE (probed 2026-09-29 against SQL Server 2025, delegations or
    /// none, table, column, schema and type scope alike).
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
        // A REVOKE takes back only what its grantor granted: dbo's REVOKE
        // leaves a row a grantee granted under its grant option (probed
        // 2026-10-04 against SQL Server 2025).
        bool Revokes(DatabasePermission p, PermissionState state) => Matches(p, state) && p.GrantorPrincipalId == grantorId;

        switch (kind)
        {
            case PermissionStatementKind.Grant:
                // A securable holds one row per grantee and permission, so
                // GRANT replaces whatever state the triple had — a DENY
                // included, and a plain GRANT after a WITH GRANT OPTION
                // downgrades W→G (probed 2026-09-28 against SQL Server 2025).
                _ = database.Permissions.RemoveAll(p => Matches(p, PermissionState.Grant) || Matches(p, PermissionState.GrantWithGrantOption) || Matches(p, PermissionState.Deny) || Matches(p, PermissionState.Revoke));
                database.Permissions.Add(new DatabasePermission(
                    permClass, permMajorId, minorId, granteePrincipalId: granteeId,
                    grantorPrincipalId: grantorId, permission: permEnum,
                    state: withGrantOption ? PermissionState.GrantWithGrantOption : PermissionState.Grant, permissionName: storedName));
                break;

            case PermissionStatementKind.Deny:
                // DENY likewise replaces the triple's GRANT as well as a prior
                // DENY (probed 2026-09-28 against SQL Server 2025); with
                // CASCADE it also takes back what the grantee granted on.
                _ = database.Permissions.RemoveAll(p => Matches(p, PermissionState.Grant) || Matches(p, PermissionState.GrantWithGrantOption) || Matches(p, PermissionState.Deny) || Matches(p, PermissionState.Revoke));
                database.Permissions.Add(new DatabasePermission(
                    permClass, permMajorId, minorId, granteePrincipalId: granteeId,
                    grantorPrincipalId: grantorId, permission: permEnum, state: PermissionState.Deny, permissionName: storedName));
                if (cascade)
                    CascadeRemoveDelegations(database, granteeId, permClass, permMajorId, permName);
                break;

            case PermissionStatementKind.Revoke when revokeGrantOptionOnly:
                // REVOKE GRANT OPTION FOR: downgrade W→G. With CASCADE, also
                // remove the rows this grantee delegated. Without CASCADE, a
                // W row with delegations raises Msg 4611.
                var wRow = database.Permissions.Find(p => Revokes(p, PermissionState.GrantWithGrantOption));
                if (wRow is null)
                    return;
                if (!cascade)
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
                var grantable = database.Permissions.Find(p => Revokes(p, PermissionState.GrantWithGrantOption));
                if (grantable is not null && !cascade)
                    throw SimulatedSqlException.RevokeRequiresCascade();
                _ = database.Permissions.RemoveAll(p => Revokes(p, PermissionState.Grant) || Revokes(p, PermissionState.GrantWithGrantOption) || Revokes(p, PermissionState.Deny) || Matches(p, PermissionState.Revoke));
                // A column revoked out of the same grantee's table-level grant
                // is remembered as an R row, which keeps that grant (and the
                // grantee's schema and database ones) off the column (probed
                // 2026-10-04 against SQL Server 2025).
                if (minorId != 0 && database.Permissions.Exists(p =>
                    p.MinorId == 0 && p.GranteePrincipalId == granteeId && p.State is PermissionState.Grant or PermissionState.GrantWithGrantOption
                    && p.IsFor(permClass, permMajorId, permEnum, permName, database)))
                {
                    database.Permissions.Add(new DatabasePermission(
                        permClass, permMajorId, minorId, granteePrincipalId: granteeId,
                        grantorPrincipalId: grantorId, permission: permEnum, state: PermissionState.Revoke, permissionName: storedName));
                }
                if (cascade)
                    CascadeRemoveDelegations(database, granteeId, permClass, permMajorId, permName);
                break;
        }
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
            // A grant option reaches its own permission; one a covering
            // permission carries (CONTROL's) is the owner's authority instead,
            // which HoldsOwnerAuthority answers and records under the owner.
            if (row.Permission == requested)
                return true;
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
        // RECEIVE is a queue's permission, which no modeled object kind is.
        if (permName.Equals("RECEIVE", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.PermissionIncompatibleWithObject("RECEIVE");
        // A sequence takes UPDATE, the permission NEXT VALUE FOR reads
        // (probed 2026-09-28 against SQL Server 2025).
        if (isDml && !kindIsTabular && !(objectTypeCode == "SO" && permName.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)))
            throw SimulatedSqlException.PermissionIncompatibleWithObject(permName.ToUpperInvariant());
        if (isExecute && !kindIsExecutable)
            throw SimulatedSqlException.PermissionIncompatibleWithObject(permName.ToUpperInvariant());
        // UNMASK reaches a table's own columns only; a view reads through its
        // base table's grant (probed 2026-09-27 against SQL Server 2025).
        if (permName.Equals("UNMASK", StringComparison.OrdinalIgnoreCase) && objectTypeCode != "U ")
            throw SimulatedSqlException.PermissionIncompatibleWithObject("UNMASK");
    }

    /// <summary>
    /// Whether <paramref name="permName"/> is one a type (or XML schema
    /// collection, or full-text catalog) is granted: <c>CONTROL</c>,
    /// <c>REFERENCES</c>, <c>TAKE OWNERSHIP</c> and <c>VIEW DEFINITION</c> on
    /// all three, <c>EXECUTE</c> on a type and an XML schema collection, and
    /// <c>ALTER</c> on an XML schema collection and a full-text catalog
    /// (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static bool PermissionAcceptedOnDerivedClass(byte permClass, string permName) =>
        BuiltInToken.EqualsAny(permName.Trim(), "CONTROL", "REFERENCES", "TAKE OWNERSHIP", "VIEW DEFINITION")
        || (permClass != PermissionChecker.ClassFulltextCatalog && BuiltInToken.Equals(permName.Trim(), "EXECUTE"))
        || (permClass != PermissionChecker.ClassType && BuiltInToken.Equals(permName.Trim(), "ALTER"));

    /// <summary>
    /// Resolves a <c>TYPE::</c>, <c>XML SCHEMA COLLECTION::</c> or
    /// <c>FULLTEXT CATALOG::</c> securable to the id its permission rows key on
    /// (<c>user_type_id</c>, <c>xml_collection_id</c>, <c>fulltext_catalog_id</c>).
    /// A missing one is Msg 15151 naming the leaf (the catalog's name as
    /// written, which takes no qualifier).
    /// </summary>
    private static int ResolveDerivedSecurable(ParserContext context, Database database, byte permClass, MultiPartName name)
    {
        var batch = context.Batch;
        switch (permClass)
        {
            case PermissionChecker.ClassType:
                // A built-in type is not a securable: real reads `sys.int` as a missing object.
                if (BuiltInToken.Equals(name.ImmediateQualifier ?? "", "sys"))
                    throw SimulatedSqlException.CannotFindObject(name.Leaf);
                if (batch.TryResolveCallerTypeSchema(name, out var typeSchema))
                {
                    if (typeSchema.AliasTypes.TryGetValue(name.Leaf, out var alias))
                        return alias.UserTypeId;
                    if (typeSchema.TableTypes.TryGetValue(name.Leaf, out var tableType))
                        return tableType.UserTypeId;
                }
                // A built-in type's name reads as a missing object (probed
                // 2026-10-04 against SQL Server 2025).
                throw name.Count == 1 && Storage.SqlType.IsSystemTypeName(name.Leaf)
                    ? SimulatedSqlException.CannotFindObject(name.Leaf)
                    : SimulatedSqlException.CannotFindType(name.Leaf);
            case PermissionChecker.ClassXmlSchemaCollection:
                return batch.TryResolveXmlSchemaCollectionSchema(name, out var collectionSchema) && collectionSchema.XmlSchemaCollections.TryGetValue(name.Leaf, out var collection)
                    ? collection.Id
                    : throw SimulatedSqlException.CannotFindXmlSchemaCollection(name.Leaf);
            default:
                return name.Count == 1 && database.FullTextCatalogs.TryGetValue(name.Leaf, out var catalog)
                    ? catalog.Id
                    : throw SimulatedSqlException.CannotFindSecurable("fulltext catalog", name.ToString());
        }
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
