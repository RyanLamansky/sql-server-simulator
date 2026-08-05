using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Guards the per-<see cref="Simulation"/> token memo: the tokenized form of
/// a command text, reused by every later execution of the same text under the
/// same tokenization inputs. Unlike the plan cache it serves every statement
/// kind — including the DML that parses and executes in one pass and so has no
/// plan to cache — because it only removes the character scan, leaving the
/// parse to run fresh each time.
/// </summary>
[TestClass]
public sealed class TokenMemoTests
{
    private static (Simulation Sim, SimulatedDbConnection Connection) OpenWithTable()
    {
        var sim = new Simulation();
        var connection = sim.CreateDbConnection();
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = """
            create table t (id int not null primary key, val int not null);
            insert t values (1, 10), (2, 20), (3, 30);
            """;
        _ = setup.ExecuteNonQuery();
        return (sim, connection);
    }

    private static List<object?> Run(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<object?>();
        do
        {
            while (reader.Read())
                values.Add(reader.FieldCount > 0 ? reader.GetValue(0) : null);
        }
        while (reader.NextResult());
        return values;
    }

    [TestMethod]
    public void RepeatedDmlBatch_SecondExecutionReadsTheMemo()
    {
        // The shape the plan cache can't serve: a modification batch. It
        // re-parses every time, but tokenizes only once.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            const string sql = "insert t values (4, 40);";
            var hitsBefore = sim.TokenMemo.Hits;
            _ = Run(connection, sql);
            AreEqual(hitsBefore, sim.TokenMemo.Hits);

            _ = Run(connection, "delete t where id = 4;");
            _ = Run(connection, sql);
            AreEqual(hitsBefore + 1, sim.TokenMemo.Hits);
        }
    }

    [TestMethod]
    public void MemoizedAndFreshExecutions_ProduceIdenticalResults()
    {
        // Every token shape in one text: delimited and bracketed identifiers,
        // string and unicode and binary and numeric literals, operators,
        // comments and an ODBC escape. The memoized replay must reproduce the
        // first execution exactly. The trailing DML keeps the plan cache out
        // of it, so the memo is what the second execution reads.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            const string sql = """
                select [t].val + 1 /* block */ , 'a''b', N'ñ', 0x00FF, 12.50, -3, {d '2020-01-02'}
                from t as [t] -- line comment
                where "t".id in (1, 2) and val <> 99
                order by [t].id desc;
                update t set val = val where id = 1;
                """;
            var first = Run(connection, sql);
            var memoHits = sim.TokenMemo.Hits;
            var second = Run(connection, sql);
            AreNotEqual(memoHits, sim.TokenMemo.Hits, "second execution should read the memo");
            CollectionAssert.AreEqual(first, second);
        }
    }

    [TestMethod]
    public void QuotedIdentifierChange_GetsItsOwnEntry()
    {
        // QUOTED_IDENTIFIER decides whether "x" is an identifier or a string,
        // so it is part of the memo's identity: the same text under each
        // setting tokenizes differently and must not share a sequence.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            const string sql = """select "val" from t where id = 1""";
            CollectionAssert.AreEqual(new List<object?> { 10 }, Run(connection, sql));

            _ = Run(connection, "set quoted_identifier off");
            // Same text, other setting: "val" is now a varchar literal.
            CollectionAssert.AreEqual(new List<object?> { "val" }, Run(connection, sql));

            _ = Run(connection, "set quoted_identifier on");
            CollectionAssert.AreEqual(new List<object?> { 10 }, Run(connection, sql));
            IsGreaterThanOrEqualTo(2, sim.TokenMemo.Count, "each setting needs its own entry");
        }
    }

    [TestMethod]
    public void BatchThatFlipsQuotedIdentifier_IsNeverMemoized()
    {
        // A text whose own SET changes the setting partway has no single
        // correct token sequence — the halves tokenize differently — so it is
        // refused publication however many times it runs.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            const string sql = """set quoted_identifier off; select "val" from t where id = 1;""";
            for (var i = 0; i < 3; i++)
                CollectionAssert.AreEqual(new List<object?> { "val" }, Run(connection, sql));
            _ = Run(connection, "set quoted_identifier on");

            var hitsBefore = sim.TokenMemo.Hits;
            CollectionAssert.AreEqual(new List<object?> { "val" }, Run(connection, sql));
            AreEqual(hitsBefore, sim.TokenMemo.Hits, "the flipping text must never be served from the memo");
        }
    }

    [TestMethod]
    public void TokenizerError_StaysAtTheSameCharacterOnEveryExecution()
    {
        // A text the tokenizer refuses never completes a sequence, so it is
        // never published — which is what keeps its error firing identically
        // rather than being replaced by a memo of the prefix.
        var (_, connection) = OpenWithTable();
        using (connection)
        {
            const string sql = "select val from t where id = 'unterminated";
            var first = ThrowsExactly<SimulatedSqlException>(() => Run(connection, sql));
            var second = ThrowsExactly<SimulatedSqlException>(() => Run(connection, sql));
            AreEqual(first.Number, second.Number);
            AreEqual(first.Message, second.Message);
        }
    }

    [TestMethod]
    public void BackAndForthLookahead_MemoizesTheSameSequenceItParsed()
    {
        // A SELECT with a FROM clause makes the parser scan ahead, rewind to
        // re-read the select list, then jump forward again. The memo records
        // tokens by ordinal for exactly this reason; an appending collector
        // would leave the sequence spliced, and the replay would parse to
        // something else. The trailing DML declines the plan cache, so every
        // execution really does re-parse from the memo.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            const string sql = "select val * 2 + id, id from t where val > 5 order by id; update t set val = val where id = 3;";
            var first = Run(connection, sql);
            var second = Run(connection, sql);
            var third = Run(connection, sql);
            IsGreaterThan(0, sim.TokenMemo.Hits);
            CollectionAssert.AreEqual(first, second);
            CollectionAssert.AreEqual(first, third);
        }
    }

    [TestMethod]
    public void ModuleBody_ReusesItsMemoAcrossInvocations()
    {
        // A procedure body is re-tokenized and re-parsed on every call, so it
        // is one of the memo's larger wins — and it goes through a synthesized
        // command whose text is the body, hitting the same store.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            _ = Run(connection, "create procedure dbo.p as begin select val from t where id = 2; end");
            var first = Run(connection, "exec dbo.p");
            var hitsBefore = sim.TokenMemo.Hits;
            var second = Run(connection, "exec dbo.p");
            IsGreaterThan(hitsBefore + 1, sim.TokenMemo.Hits, "both the EXEC batch and the body should be served");
            CollectionAssert.AreEqual(first, second);
        }
    }

    [TestMethod]
    public void ConcurrentExecutions_ShareOneSequenceSafely()
    {
        // Memo entries are shared across sessions like cached plans are. The
        // tokens are immutable, so the only question is whether readers agree
        // — which is what this hammers.
        var sim = new Simulation();
        using (var setupConnection = sim.CreateDbConnection())
        {
            setupConnection.Open();
            using var setup = setupConnection.CreateCommand();
            setup.CommandText = "create table t (id int not null primary key, val int not null); insert t values (1, 10), (2, 20), (3, 30);";
            _ = setup.ExecuteNonQuery();
        }

        const string sql = "select val from t where id > 1 order by id; update t set val = val where id = 1;";
        var failures = 0;
        _ = Parallel.For(0, 8, _ =>
        {
            using var connection = sim.CreateDbConnection();
            connection.Open();
            for (var i = 0; i < 60; i++)
            {
                var values = Run(connection, sql);
                if (values.Count != 2 || !Equals(values[0], 20) || !Equals(values[1], 30))
                    _ = Interlocked.Increment(ref failures);
            }
        });

        AreEqual(0, failures);
    }
}
