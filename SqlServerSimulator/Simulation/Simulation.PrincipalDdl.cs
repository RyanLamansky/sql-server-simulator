using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE USER name [{FOR | FROM} LOGIN name | WITHOUT LOGIN |
    /// WITH PASSWORD = '…' | …] [WITH DEFAULT_SCHEMA = name …]</c>. The
    /// simulator has no permission enforcement; only the principal name +
    /// allocated id land in <see cref="Database.Principals"/> for catalog-
    /// view round-trip. The post-name grammar (FROM LOGIN / WITH PASSWORD /
    /// FROM EXTERNAL PROVIDER / DEFAULT_SCHEMA / etc.) parses-and-discards
    /// up to the next statement boundary.
    /// </summary>
    /// <remarks>
    /// Returns true on success so the dispatch loop's match-when-success
    /// pattern fires; pre-existing principal name raises Msg 15023 verbatim.
    /// </remarks>
    internal static bool TryParseCreateUser(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        ConsumeToStatementBoundary(context);
        if (context.Batch.IsSkipping)
            return true;
        if (context.CurrentDatabase.Principals.ContainsKey(name))
            throw SimulatedSqlException.PrincipalAlreadyExists(name);
        var id = context.CurrentDatabase.AllocatePrincipalId();
        context.CurrentDatabase.Principals[name] = new DatabasePrincipal(
            id, name, "S", "SQL_USER", isFixedRole: false, context.Batch.CurrentStatement.UtcNow);
        return true;
    }

    /// <summary>
    /// Parses <c>CREATE ROLE name [AUTHORIZATION owner]</c>. Like
    /// <see cref="TryParseCreateUser"/>, only the role name + id land in
    /// the catalog; the AUTHORIZATION clause parse-and-discards. The
    /// post-create role is empty (no members) until
    /// <see cref="TryParseAlterRole"/> adds them.
    /// </summary>
    internal static bool TryParseCreateRole(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        ConsumeToStatementBoundary(context);
        if (context.Batch.IsSkipping)
            return true;
        if (context.CurrentDatabase.Principals.ContainsKey(name))
            throw SimulatedSqlException.PrincipalAlreadyExists(name);
        var id = context.CurrentDatabase.AllocatePrincipalId();
        context.CurrentDatabase.Principals[name] = new DatabasePrincipal(
            id, name, "R", "DATABASE_ROLE", isFixedRole: false, context.Batch.CurrentStatement.UtcNow);
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER ROLE name { ADD MEMBER name | DROP MEMBER name |
    /// WITH NAME = newname }</c>. ADD/DROP MEMBER mutates
    /// <see cref="Database.RoleMembers"/>; WITH NAME parse-and-discards
    /// (the simulator's principal dict is keyed by name so a rename
    /// requires care that AW doesn't need).
    /// </summary>
    internal static bool TryParseAlterRole(ParserContext context)
    {
        // Cursor on ROLE (caller has already matched ALTER + ROLE).
        context.MoveNextRequired();
        if (context.Token is not Name roleNameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var roleName = roleNameToken.Value;
        context.MoveNextRequired();

        // Action: ADD MEMBER / DROP MEMBER / WITH NAME = ... ADD and DROP
        // are reserved keywords; MEMBER is a bare identifier (UnquotedString).
        if (context.Token is ReservedKeyword { Keyword: Keyword.Add or Keyword.Drop } addOrDrop)
        {
            var isAdd = addOrDrop.Keyword == Keyword.Add;
            context.MoveNextRequired();
            if (context.Token is not UnquotedString { Value: var memberWord }
                || !memberWord.Equals("MEMBER", StringComparison.OrdinalIgnoreCase))
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
            if (context.Token is not Name memberNameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var memberName = memberNameToken.Value;
            context.MoveNextOptional();

            if (context.Batch.IsSkipping)
                return true;
            if (!context.CurrentDatabase.Principals.TryGetValue(roleName, out var role))
                throw SimulatedSqlException.CannotFindPrincipal(roleName);
            if (!context.CurrentDatabase.Principals.TryGetValue(memberName, out var member))
                throw SimulatedSqlException.CannotFindPrincipal(memberName);
            if (isAdd)
            {
                if (!context.CurrentDatabase.RoleMembers.Contains((role.PrincipalId, member.PrincipalId)))
                    context.CurrentDatabase.RoleMembers.Add((role.PrincipalId, member.PrincipalId));
            }
            else
            {
                _ = context.CurrentDatabase.RoleMembers.Remove((role.PrincipalId, member.PrincipalId));
            }
            return true;
        }

        // WITH NAME = newname — parse-and-discard.
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            ConsumeToStatementBoundary(context);
            return true;
        }
        throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Parses <c>DROP USER [IF EXISTS] name</c>. Routes to the per-database
    /// principal dict (rather than the per-schema object dict that the
    /// generic <c>DROP &lt;target&gt;</c> path handles).
    /// </summary>
    internal static bool TryParseDropUser(ParserContext context)
    {
        context.MoveNextRequired();
        var ifExists = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.If })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Exists })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ifExists = true;
            context.MoveNextRequired();
        }
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        context.MoveNextOptional();
        if (context.Batch.IsSkipping)
            return true;
        if (!context.CurrentDatabase.Principals.TryRemove(name, out var removed))
        {
            return ifExists ? true : throw SimulatedSqlException.CannotFindPrincipal(name);
        }
        // Cascade: drop role memberships that reference the removed principal.
        _ = context.CurrentDatabase.RoleMembers.RemoveAll(rm =>
            rm.RoleId == removed.PrincipalId || rm.MemberId == removed.PrincipalId);
        return true;
    }

    /// <summary>
    /// Parses <c>DROP ROLE [IF EXISTS] name</c>. Same shape as
    /// <see cref="TryParseDropUser"/>; the principal dict is shared.
    /// </summary>
    internal static bool TryParseDropRole(ParserContext context) => TryParseDropUser(context);

    /// <summary>
    /// Consumes tokens through end-of-batch or the next <c>;</c> /
    /// statement-starting keyword. Used by the parse-and-discard tails
    /// (FROM LOGIN / WITH PASSWORD / DEFAULT_SCHEMA / etc.) that the
    /// simulator doesn't model. Leaves the cursor on the boundary token.
    /// </summary>
    private static void ConsumeToStatementBoundary(ParserContext context)
    {
        while (!IsStatementBoundary(context.Token))
            context.MoveNextOptional();
    }
}
