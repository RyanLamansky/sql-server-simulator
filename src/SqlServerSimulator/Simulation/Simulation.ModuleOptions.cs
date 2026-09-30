using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>The module kind whose <c>WITH</c> option clause is being read.</summary>
    private enum ModuleOptionHost : byte
    {
        ScalarFunction,
        InlineFunction,
        TableFunction,
        Procedure,
        View,
        Trigger,
    }

    /// <summary>The options one module's <c>WITH</c> clause set.</summary>
    private sealed class ModuleOptions
    {
        public bool Encryption;
        public bool SchemaBinding;
        public bool NativeCompilation;
        public bool ReturnsNullOnNullInput;
        public string? ExecuteAs;

        /// <summary>
        /// The first option written that a CLR module refuses —
        /// <c>ENCRYPTION</c>, <c>RECOMPILE</c> or <c>NATIVE_COMPILATION</c> —
        /// for the <c>EXTERNAL NAME</c> tail to report once it knows the
        /// module is one.
        /// </summary>
        public string? RefusedByExternalModule;
    }

    /// <summary>
    /// Msg 1054 for a <c>GROUP BY ALL</c> anywhere in a schema-bound view's or
    /// function's body, which runs from the cursor to the end of the batch. Real
    /// raises it while parsing, at the <c>ALL</c>, ahead of anything the body
    /// binds (probed 2026-09-30 against SQL Server 2025), so the body's tokens
    /// are scanned before it parses. Leaves the cursor where it was.
    /// </summary>
    private static void RejectGroupByAllInSchemaBoundBody(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            Token? previous = null, beforePrevious = null;
            for (var token = context.Token; token is not null; token = context.GetNextOptional())
            {
                if (token is ReservedKeyword { Keyword: Keyword.All }
                    && previous is ReservedKeyword { Keyword: Keyword.By }
                    && beforePrevious is ReservedKeyword { Keyword: Keyword.Group })
                {
                    throw SimulatedSqlException.SyntaxNotAllowedInSchemaBoundObject("ALL", 8, token.LineNumber);
                }
                (beforePrevious, previous) = (previous, token);
            }
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>
    /// Reads a module's optional <c>WITH option [, option …]</c> clause,
    /// leaving the cursor on the token after it, which must be the one the
    /// host's grammar continues with. A function's clause and every other
    /// module's are two grammars (probed 2026-09-26 against SQL Server 2025):
    /// only a function's reads <c>RETURNS NULL ON NULL INPUT</c>,
    /// <c>CALLED ON NULL INPUT</c> and <c>INLINE = ON | OFF</c> as options,
    /// and only the other's recognizes <c>RECOMPILE</c> and
    /// <c>VIEW_METADATA</c>. The clause is judged once it has parsed — so a
    /// syntax error after it wins — in written order: a repeated option is
    /// Msg 1039, an unrecognized one Msg 195, one the host doesn't take Msg
    /// 487, and after them <c>SCHEMABINDING</c> without
    /// <c>NATIVE_COMPILATION</c> where only natively compiled modules take it,
    /// or the reverse, is Msg 10796.
    /// </summary>
    private static ModuleOptions ParseModuleOptions(ParserContext context, ModuleOptionHost host, string moduleName)
    {
        var options = new ModuleOptions();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return options;

        var functionGrammar = host is ModuleOptionHost.ScalarFunction or ModuleOptionHost.InlineFunction or ModuleOptionHost.TableFunction;
        var written = new List<(string Name, bool Recognized)>();
        // Long enough for every recognized word; a longer one is unrecognized.
        Span<char> upper = stackalloc char[32];
        context.MoveNextRequired();
        while (true)
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Execute or Keyword.Exec }:
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    options.ExecuteAs = context.Token switch
                    {
                        Name principal => principal.Value,
                        Literal { Value: { IsNull: false } quoted } => quoted.AsString,
                        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                    };
                    context.MoveNextRequired();
                    written.Add(("EXECUTE AS", true));
                    break;
                case ReservedKeyword { Keyword: Keyword.For } when host == ModuleOptionHost.Procedure:
                    // A procedure's FOR REPLICATION, which real's grammar puts
                    // after the option list; the list accepts it in place.
                    if (context.GetNextRequired() is not (ReservedKeyword { Keyword: Keyword.Replication } or UnquotedString { ContextualKeyword: ContextualKeyword.Replication }))
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    written.Add(("FOR REPLICATION", true));
                    break;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Returns } when functionGrammar:
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    ExpectOnNullInput(context);
                    options.ReturnsNullOnNullInput = true;
                    written.Add(("RETURNS NULL ON NULL INPUT", true));
                    break;
                case Name { Value: var called } when functionGrammar && BuiltInToken.Equals(called, "CALLED"):
                    ExpectOnNullInput(context);
                    written.Add(("CALLED ON NULL INPUT", true));
                    break;
                case Name { Value: var inline } when functionGrammar && BuiltInToken.Equals(inline, "INLINE"):
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    var setting = context.GetNextRequired();
                    if (setting is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    written.Add(("INLINE", true));
                    break;
                case Name { Value: var word }:
                    var folded = word.Length <= upper.Length ? upper[..word.AsSpan().ToUpperInvariant(upper)] : [];
                    var recognized = folded switch
                    {
                        "ENCRYPTION" or "NATIVE_COMPILATION" or "SCHEMABINDING" => true,
                        "RECOMPILE" or "VIEW_METADATA" => !functionGrammar,
                        _ => false,
                    };
                    written.Add((recognized ? folded.ToString() : word, recognized));
                    context.MoveNextRequired();
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }

        var continues = host switch
        {
            ModuleOptionHost.ScalarFunction or ModuleOptionHost.TableFunction => context.Token is ReservedKeyword { Keyword: Keyword.As or Keyword.Begin },
            ModuleOptionHost.InlineFunction => context.Token is ReservedKeyword { Keyword: Keyword.As or Keyword.Return },
            ModuleOptionHost.Trigger => context.Token is ReservedKeyword { Keyword: Keyword.For } or UnquotedString { ContextualKeyword: ContextualKeyword.After or ContextualKeyword.Instead },
            _ => context.Token is ReservedKeyword { Keyword: Keyword.As },
        };
        if (!continues)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var statement = host switch
        {
            ModuleOptionHost.Procedure => "PROCEDURE",
            ModuleOptionHost.View => "VIEW",
            ModuleOptionHost.Trigger => "TRIGGER",
            _ => "FUNCTION",
        };
        for (var i = 0; i < written.Count; i++)
        {
            var (name, recognized) = written[i];
            for (var j = 0; j < i; j++)
            {
                if (written[j].Recognized && recognized && written[j].Name == name)
                    throw SimulatedSqlException.OptionSpecifiedMoreThanOnce(name);
            }
            if (!recognized)
                throw SimulatedSqlException.OptionNotRecognized(name);
            if (InvalidOptionState(host, name) is { } state)
                throw SimulatedSqlException.InvalidOptionForCreateStatement(statement, state);
            if (name is "ENCRYPTION" or "RECOMPILE" or "NATIVE_COMPILATION")
                options.RefusedByExternalModule ??= name;
            switch (name)
            {
                case "ENCRYPTION":
                    options.Encryption = true;
                    break;
                case "NATIVE_COMPILATION":
                    options.NativeCompilation = true;
                    break;
                case "SCHEMABINDING":
                    options.SchemaBinding = true;
                    break;
            }
        }

        // SCHEMABINDING is required of a natively compiled module and taken by
        // no other procedure or trigger; a function takes it either way.
        var mismatched = host switch
        {
            ModuleOptionHost.Procedure or ModuleOptionHost.Trigger => options.SchemaBinding != options.NativeCompilation,
            ModuleOptionHost.ScalarFunction => options.NativeCompilation && !options.SchemaBinding,
            _ => false,
        };
        if (mismatched)
        {
            // Real reports it at line 16 of the module, whatever its length.
            var error = SimulatedSqlException.SchemaBindingRequiresNativeCompilation(host == ModuleOptionHost.ScalarFunction ? (byte)2 : (byte)1);
            error.PreserveDiagnostics(16, moduleName);
            throw error;
        }
        if (options.SchemaBinding && host is ModuleOptionHost.View or ModuleOptionHost.ScalarFunction or ModuleOptionHost.InlineFunction or ModuleOptionHost.TableFunction)
            RejectGroupByAllInSchemaBoundBody(context);
        return options;

        static void ExpectOnNullInput(ParserContext context)
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Input })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
    }

    // The options a host's grammar parses but the host refuses, with the
    // Msg 487 state real raises for each; null for an accepted one.
    private static byte? InvalidOptionState(ModuleOptionHost host, string option) => (host, option) switch
    {
        (ModuleOptionHost.InlineFunction, "CALLED ON NULL INPUT" or "EXECUTE AS" or "INLINE" or "NATIVE_COMPILATION" or "RETURNS NULL ON NULL INPUT") => 1,
        (ModuleOptionHost.Procedure, "VIEW_METADATA") => 1,
        (ModuleOptionHost.TableFunction, "CALLED ON NULL INPUT" or "INLINE" or "NATIVE_COMPILATION" or "RETURNS NULL ON NULL INPUT") => 1,
        (ModuleOptionHost.Trigger, "RECOMPILE" or "VIEW_METADATA") => 1,
        (ModuleOptionHost.View, "EXECUTE AS") => 2,
        (ModuleOptionHost.View, "NATIVE_COMPILATION" or "RECOMPILE") => 1,
        _ => null,
    };
}
