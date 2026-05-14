using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>GRANT &lt;perm_list&gt; [ON &lt;securable&gt;] TO &lt;principal_list&gt;
    /// [WITH GRANT OPTION] [AS &lt;grantor&gt;]</c>. The simulator has no
    /// permission enforcement; the parsed permission lands in
    /// <see cref="Database.Permissions"/> for catalog-view round-trip and
    /// is otherwise inert.
    /// </summary>
    /// <remarks>
    /// AW's bacpac emits two GRANTs (<c>GRANT VIEW ANY COLUMN ENCRYPTION
    /// KEY DEFINITION TO public</c> and <c>GRANT VIEW ANY COLUMN MASTER
    /// KEY DEFINITION TO public</c>) — both database-scope (no <c>ON</c>
    /// clause). The parser handles the broader grammar (<c>ON OBJECT::name</c>,
    /// <c>ON SCHEMA::name</c>, <c>WITH GRANT OPTION</c>, <c>AS &lt;grantor&gt;</c>)
    /// by parse-and-discard for the parts the simulator doesn't model.
    /// </remarks>
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

        // Permission list — comma-separated spelled-out permission names
        // (e.g. SELECT, UPDATE, VIEW ANY COLUMN MASTER KEY DEFINITION).
        // Each permission is a sequence of one or more bare identifier
        // tokens; the sequence ends at a comma, ON, TO, FROM, or AS.
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
        // ON SCHEMA::name, ON DATABASE::name, ON TYPE::name. For the
        // AW-baseline goal we accept-and-parse the grammar but only
        // populate class=0 (database scope) when there's no ON clause.
        byte permClass = 0;
        var permMajorId = 0;
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            context.MoveNextRequired();
            // OBJECT::name / SCHEMA::name / DATABASE::name / TYPE::name —
            // detect the `::` after the leading Name without committing to
            // a cursor advance if the operator pair isn't there.
            if (context.Token is Name)
            {
                var checkpoint = context.SaveCheckpoint();
                _ = context.GetNextOptional();
                if (context.Token is Operator { Character: ':' }
                    && context.GetNextOptional() is Operator { Character: ':' })
                {
                    context.MoveNextRequired();
                }
                else
                {
                    context.RestoreCheckpoint(checkpoint);
                }
            }
            // Consume the securable name. ParseObjectName leaves cursor on
            // the last segment; advance past for the TO/FROM lookup.
            _ = BatchContext.ParseObjectName(context);
            context.MoveNextRequired();
            // Mark as object-scope rather than database-scope. The simulator
            // doesn't resolve the object_id at this point — AW's GRANTs are
            // all database-scope so this path isn't load-bearing for the
            // baseline.
            permClass = 1;
            permMajorId = 0;
        }

        // TO <principal_list> for GRANT / DENY, FROM <principal_list> for
        // REVOKE. Real SQL Server accepts TO for REVOKE too (probe-confirmed).
        if (context.Token is not ReservedKeyword { Keyword: Keyword.To or Keyword.From })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        var granteeNames = new List<string>();
        while (true)
        {
            // `public` tokenizes as ReservedKeyword.Public, not Name —
            // accept it (and any other reserved keyword in the grantee
            // slot) by reading the raw source text via Token.ToString.
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
        if (kind == PermissionStatementKind.Revoke && context.Token is UnquotedString { Value: var revokeTrailer }
            && Collation.Default.Equals(revokeTrailer, "CASCADE"))
        {
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

        // Resolve grantees against the pre-seeded principal dict. Unknown
        // principal raises Msg 15151 (no such user / role) — matches real
        // SQL Server probe wording, abbreviated for the simulator.
        foreach (var granteeName in granteeNames)
        {
            if (!context.CurrentDatabase.Principals.TryGetValue(granteeName, out var grantee))
                throw SimulatedSqlException.CannotFindPrincipal(granteeName);
            foreach (var permName in permissions)
            {
                var state = kind switch
                {
                    PermissionStatementKind.Grant when withGrantOption => "W",
                    PermissionStatementKind.Grant => "G",
                    PermissionStatementKind.Deny => "D",
                    PermissionStatementKind.Revoke when revokeGrantOptionOnly => "G",
                    PermissionStatementKind.Revoke => "R",
                    _ => "G",
                };
                if (kind == PermissionStatementKind.Revoke)
                {
                    _ = context.CurrentDatabase.Permissions.RemoveAll(p =>
                        p.GranteePrincipalId == grantee.PrincipalId
                        && Collation.Default.Equals(p.PermissionName, permName)
                        && p.Class == permClass
                        && p.MajorId == permMajorId);
                    continue;
                }
                var typeCode = DerivePermissionTypeCode(permName);
                context.CurrentDatabase.Permissions.Add(new DatabasePermission(
                    permClass, permMajorId, minorId: 0,
                    granteePrincipalId: grantee.PrincipalId,
                    grantorPrincipalId: context.CurrentDatabase.Principals["dbo"].PrincipalId,
                    permissionName: permName,
                    typeCode: typeCode,
                    state: state));
            }
        }
        return true;
    }

    /// <summary>
    /// Returns the bare identifier text for the current token if it's part
    /// of a permission name (e.g. <c>SELECT</c>, <c>VIEW</c>, <c>ANY</c>,
    /// <c>COLUMN</c>). Returns null when the cursor reaches a clause
    /// boundary (comma / ON / TO / FROM / AS / WITH / semicolon / EOF) so
    /// the caller can dispatch to the next clause.
    /// </summary>
    /// <remarks>
    /// The grammar SQL Server uses is positionally-bounded: permission
    /// keywords aren't on the reserved list (most surface as
    /// <see cref="UnquotedString"/>), but a few are
    /// (<see cref="ReservedKeyword"/> matches like <c>SELECT</c>,
    /// <c>UPDATE</c>, <c>DELETE</c>, <c>EXECUTE</c>). This helper accepts
    /// any name-shaped token that isn't a clause-boundary keyword.
    /// </remarks>
    private static string? TryConsumePermissionWord(ParserContext context) => context.Token switch
    {
        ReservedKeyword { Keyword: Keyword.To or Keyword.From or Keyword.On or Keyword.As or Keyword.With } => null,
        Operator { Character: ',' or ';' or '(' or ')' } => null,
        null => null,
        ReservedKeyword rk => rk.ToString(),
        Name n => n.Value,
        _ => null,
    };

    /// <summary>
    /// Derives the 4-character <c>sys.database_permissions.type</c> code
    /// from a spelled-out permission name by taking the first letter of
    /// each word (right-padded with spaces). E.g. <c>VIEW ANY COLUMN
    /// MASTER KEY DEFINITION</c> → <c>VACM</c>. Approximate — real SQL
    /// Server's mapping isn't strictly first-letter (e.g. <c>SELECT</c> →
    /// <c>SL</c>, <c>UPDATE</c> → <c>UP</c>), but accurate enough for
    /// AW round-trip; a per-permission lookup table is the polish path.
    /// </summary>
    private static string DerivePermissionTypeCode(string permissionName)
    {
        var words = permissionName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Span<char> code = stackalloc char[4];
        var idx = 0;
        foreach (var w in words)
        {
            if (idx >= 4)
                break;
            code[idx++] = char.ToUpperInvariant(w[0]);
        }
        while (idx < 4)
            code[idx++] = ' ';
        return new string(code);
    }
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
