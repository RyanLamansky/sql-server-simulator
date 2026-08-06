using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Binary <c>SUBSTRING</c> and the legacy text-pointer statement trio
/// (<c>READTEXT</c> / <c>WRITETEXT</c> / <c>UPDATETEXT</c>). Every expected
/// value and every Msg / state is probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class LegacyLobTests
{
    private const string BinaryFixture = """
        create table b (id int, vb varbinary(30), bn binary(10), im image, vbm varbinary(max));
        insert b values (1, 0x0102030405060708090A, 0x0102030405, 0x0102030405060708, 0x01020304050607080910);
        """;

    [TestMethod]
    [DataRow("substring(0x0102030405060708090A, 2, 3)", "020304")]
    [DataRow("substring(0x0102030405060708090A, 0, 3)", "0102")]         // window starts before byte 1
    [DataRow("substring(0x0102030405060708090A, -2, 5)", "0102")]
    [DataRow("substring(0x0102030405060708090A, 2, 0)", "")]             // empty, not NULL
    [DataRow("substring(0x0102030405060708090A, 50, 3)", "")]            // start past the end
    [DataRow("substring(0x0102030405060708090A, 8, 100)", "08090A")]     // length clamps to the remainder
    [DataRow("substring(0x0102030405060708090A, -2147483648, 2147483647)", "")]
    public void BinarySubstring_Slices(string expression, string expectedHex) =>
        AreEqual(expectedHex, Convert.ToHexString((byte[])new Simulation().ExecuteScalar($"select {expression}")!));

    [TestMethod]
    [DataRow("substring(vb, 2, 3)", "020304")]
    [DataRow("substring(bn, 4, 4)", "04050000")]                          // binary(10) reads through its 0x00 padding
    [DataRow("substring(im, 2, 3)", "020304")]
    [DataRow("substring(vbm, 2, 3)", "020304")]
    public void BinarySubstring_OverColumns(string expression, string expectedHex)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(BinaryFixture);
        AreEqual(expectedHex, Convert.ToHexString((byte[])simulation.ExecuteScalar($"select {expression} from b")!));
    }

    [TestMethod]
    [DataRow("substring(vb, null, 3)")]
    [DataRow("substring(vb, 2, null)")]
    [DataRow("substring(cast(null as varbinary(30)), 2, 3)")]
    public void BinarySubstring_NullPropagates(string expression)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(BinaryFixture);
        _ = IsInstanceOfType<DBNull>(simulation.ExecuteScalar($"select {expression} from b"));
    }

    /// <summary>
    /// The projected width follows the same min(source width, constant length)
    /// rule the character families use, with <c>image</c> capped at 8000 and
    /// <c>varbinary(max)</c> staying MAX whatever the length argument is.
    /// </summary>
    [TestMethod]
    [DataRow("substring(vb, 2, 3)", "varbinary(3)")]
    [DataRow("substring(bn, 2, 3)", "varbinary(3)")]
    [DataRow("substring(im, 2, 3)", "varbinary(3)")]
    [DataRow("substring(vbm, 2, 3)", "varbinary(-1)")]
    [DataRow("substring(vb, 2, 9000)", "varbinary(30)")]
    [DataRow("substring(bn, 2, 9000)", "varbinary(10)")]
    [DataRow("substring(vb, 2, 0)", "varbinary(1)")]
    public void BinarySubstring_ProjectedType(string expression, string expected) =>
        AreEqual(expected, ProjectedDeclaration($"select {expression} as s", BinaryFixture));

    /// <summary>
    /// A non-constant length leaves the width at the source's, and
    /// <c>image</c>'s is the 8000-byte family container.
    /// </summary>
    [TestMethod]
    [DataRow("vb", "varbinary(30)")]
    [DataRow("im", "varbinary(8000)")]
    public void BinarySubstring_VariableLength_ProjectsSourceWidth(string column, string expected) =>
        AreEqual(expected, ProjectedDeclaration($"declare @n int = 3; select substring({column}, 2, @n) as s", BinaryFixture));

    /// <summary>
    /// The declared type a projection lands in a <c>SELECT … INTO</c>
    /// destination, which is how the simulator's own public surface exposes the
    /// width an expression projects (real derives the destination declaration
    /// from the same inference).
    /// </summary>
    private static string ProjectedDeclaration(string projection, string fixture)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(fixture);
        _ = simulation.ExecuteNonQuery($"{projection} into d from b");
        return (string)simulation.ExecuteScalar("""
            select ty.name + '(' + cast(c.max_length as varchar(10)) + ')'
            from sys.columns c join sys.types ty on ty.user_type_id = c.system_type_id
            where c.object_id = object_id('d')
            """)!;
    }

    /// <summary>
    /// A constant negative length is settled while compiling (Msg 536, state 8
    /// for <c>SUBSTRING</c> and 6 for <c>LEFT</c> / <c>RIGHT</c>); one that only
    /// turns negative at run time reports Msg 537 for <c>LEFT</c> /
    /// <c>SUBSTRING</c> and Msg 536 state 2 for <c>RIGHT</c>.
    /// </summary>
    [TestMethod]
    [DataRow("select substring(0x0102, 1, -1)", 536, (byte)8, "Invalid length parameter passed to the substring function.")]
    [DataRow("select left('abc', -1)", 536, (byte)6, "Invalid length parameter passed to the left function.")]
    [DataRow("select right('abc', -1)", 536, (byte)6, "Invalid length parameter passed to the right function.")]
    [DataRow("declare @n int = -1; select substring(0x0102, 1, @n)", 537, (byte)2, "Invalid length parameter passed to the LEFT or SUBSTRING function.")]
    [DataRow("declare @n int = -1; select substring('abc', 1, @n)", 537, (byte)2, "Invalid length parameter passed to the LEFT or SUBSTRING function.")]
    [DataRow("declare @n int = -1; select left('abc', @n)", 537, (byte)2, "Invalid length parameter passed to the LEFT or SUBSTRING function.")]
    [DataRow("declare @n int = -1; select right('abc', @n)", 536, (byte)2, "Invalid length parameter passed to the RIGHT function.")]
    public void NegativeLength_SplitsBetweenCompileAndRuntime(string sql, int number, byte state, string message)
    {
        var ex = new Simulation().AssertSqlError(sql, number);
        AreEqual(state, ex.State);
        AreEqual(message, ex.Message);
    }

    /// <summary>
    /// A legacy LOB source narrows to the bounded var* family a constant length
    /// names, matching real: <c>SUBSTRING(&lt;text&gt;, 2, 3)</c> projects
    /// <c>varchar(3)</c> rather than <c>text</c>.
    /// </summary>
    [TestMethod]
    [DataRow("substring(tx, 2, 3)", "varchar(3)")]
    [DataRow("substring(nt, 2, 3)", "nvarchar(6)")]
    public void LegacyLobSubstring_ProjectsBoundedFamily(string expression, string expected) =>
        AreEqual(expected, ProjectedDeclaration(
            $"select {expression} as s",
            "create table b (tx text, nt ntext); insert b values ('abcdef', N'abcdef');"));

    private const string LobFixture = """
        create table t (id int primary key, tx text, nt ntext, im image);
        insert t values (1, 'Hello world, this is a text column.', N'Ünicode ntext here.', 0x0102030405060708);
        insert t values (2, null, null, null);
        """;

    /// <summary>
    /// Runs <paramref name="statements"/> against the fixture with
    /// <c>@p</c> already holding the pointer of row <paramref name="id"/>'s
    /// <paramref name="column"/>, and answers the first value the batch
    /// returned.
    /// </summary>
    private static object? WithPointer(string column, int id, string statements, string fixture = LobFixture)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(fixture);
        return simulation.ExecuteScalar($"declare @p varbinary(16); select @p = textptr({column}) from t where id = {id}; {statements}");
    }

    /// <summary>
    /// A pointer identifies the row it was read from, so two rows of one column
    /// carry different pointers and reading one twice carries the same.
    /// </summary>
    [TestMethod]
    public void TextPointer_IdentifiesTheRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, tx text); insert t values (1, 'first row'), (2, 'second row');");
        AreEqual(0, simulation.ExecuteScalar<int>("""
            declare @a varbinary(16), @b varbinary(16);
            select @a = textptr(tx) from t where id = 1;
            select @b = textptr(tx) from t where id = 2;
            select case when @a = @b then 1 else 0 end;
            """));
        AreEqual(1, simulation.ExecuteScalar<int>("""
            declare @a varbinary(16), @b varbinary(16);
            select @a = textptr(tx) from t where id = 1;
            select @b = textptr(tx) from t where id = 1;
            select case when @a = @b then 1 else 0 end;
            """));
    }

    /// <summary>
    /// <c>READTEXT</c>'s offset and size count bytes for <c>text</c> and
    /// <c>image</c> and characters for <c>ntext</c>; a size of 0 reads to the
    /// end of the value.
    /// </summary>
    [TestMethod]
    [DataRow("tx", "readtext t.tx @p 0 5", "Hello")]
    [DataRow("tx", "readtext t.tx @p 6 5", "world")]
    [DataRow("tx", "readtext t.tx @p 0 0", "Hello world, this is a text column.")]
    [DataRow("tx", "readtext t.tx @p 35 0", "")]
    [DataRow("tx", "readtext t.tx @p 0 5 holdlock", "Hello")]
    [DataRow("nt", "readtext t.nt @p 0 3", "Üni")]
    [DataRow("nt", "readtext t.nt @p 1 3", "nic")]
    public void ReadText_ReadsWindow(string column, string statement, string expected) =>
        AreEqual(expected, WithPointer(column, 1, statement));

    [TestMethod]
    public void ReadText_Image_CountsBytes() =>
        AreEqual("030405", Convert.ToHexString((byte[])WithPointer("im", 1, "readtext t.im @p 2 3")!));

    /// <summary>
    /// Offset and size are validated against the value's own length, real
    /// naming that length in Msg 7124.
    /// </summary>
    [TestMethod]
    [DataRow("readtext t.tx @p 1000 5")]
    [DataRow("readtext t.tx @p 30 100")]
    [DataRow("readtext t.tx @p 36 0")]
    public void ReadText_WindowPastData_RaisesMsg7124(string statement)
    {
        var ex = Throws<SimulatedSqlException>(() => WithPointer("tx", 1, statement));
        AreEqual(7124, ex.Number);
        AreEqual("The offset and length specified in the READTEXT statement is greater than the actual data length of 35.", ex.Message);
    }

    /// <summary>A written sign is refused by the grammar itself.</summary>
    [TestMethod]
    [DataRow("readtext t.tx @p -1 5")]
    [DataRow("readtext t.tx @p 0 -5")]
    public void ReadText_NegativeWindow_RaisesMsg102(string statement)
    {
        var ex = Throws<SimulatedSqlException>(() => WithPointer("tx", 1, statement));
        AreEqual(102, ex.Number);
        AreEqual("Incorrect syntax near '-'.", ex.Message);
    }

    /// <summary>
    /// A variable is the only way to reach a negative window, and real reads
    /// the two halves differently: a negative offset is Msg 7116 at state 3,
    /// while a negative size reads to the end exactly as 0 does. A NULL offset
    /// reads from the start.
    /// </summary>
    [TestMethod]
    public void ReadText_VariableWindow_RuntimeRules()
    {
        AreEqual(
            "Hello world, this is a text column.",
            WithPointer("tx", 1, "declare @o int = 0, @s int = -5; readtext t.tx @p @o @s"));
        AreEqual(
            "Hello",
            WithPointer("tx", 1, "declare @o int, @s int = 5; readtext t.tx @p @o @s"));
        var ex = Throws<SimulatedSqlException>(() =>
            WithPointer("tx", 1, "declare @o int = -1, @s int = 5; readtext t.tx @p @o @s"));
        AreEqual(7116, ex.Number);
        AreEqual((byte)3, ex.State);
        AreEqual("Offset -1 is not in the range of available LOB data.", ex.Message);
    }

    [TestMethod]
    public void WriteText_ReplacesWholeValue() =>
        AreEqual("REPLACED", WithPointer("tx", 1, "writetext t.tx @p 'REPLACED'; select cast(tx as varchar(50)) from t where id = 1"));

    [TestMethod]
    public void WriteText_WithLog_ReplacesWholeValue() =>
        AreEqual("LOGGED", WithPointer("tx", 1, "writetext t.tx @p with log 'LOGGED'; select cast(tx as varchar(50)) from t where id = 1"));

    [TestMethod]
    public void WriteText_NullValue_ClearsCell() =>
        IsNull(WithPointer("tx", 1, "writetext t.tx @p null; select cast(tx as varchar(50)) from t where id = 1") as string);

    [TestMethod]
    public void WriteText_Ntext_ConvertsToUnicode() =>
        AreEqual("plain ansi", WithPointer("nt", 1, "writetext t.nt @p 'plain ansi'; select cast(nt as nvarchar(50)) from t where id = 1"));

    [TestMethod]
    public void WriteText_Image_TakesBinaryLiteral() =>
        AreEqual("AABBCC", Convert.ToHexString((byte[])WithPointer("im", 1, "writetext t.im @p 0xAABBCC; select im from t where id = 1")!));

    /// <summary>
    /// A cell that was never written has no pointer, so <c>TEXTPTR</c> hands
    /// back NULL and the write refuses it — real's Msg 7133, which is what
    /// forces the initialize-with-an-empty-value dance.
    /// </summary>
    [TestMethod]
    [DataRow("writetext t.tx @p 'x'", "WRITE TEXT", (byte)2)]
    [DataRow("updatetext t.tx @p 0 0 'x'", "UPDATE TEXT", (byte)2)]
    [DataRow("readtext t.tx @p 0 1", "READ TEXT", (byte)1)]
    public void NullPointer_RaisesMsg7133(string statement, string utility, byte state)
    {
        var ex = Throws<SimulatedSqlException>(() => WithPointer("tx", 2, statement));
        AreEqual(7133, ex.Number);
        AreEqual(state, ex.State);
        AreEqual($"NULL textptr (text, ntext, or image pointer) passed to {utility} function.", ex.Message);
    }

    [TestMethod]
    public void WriteText_AfterInitializingToEmpty_Writes() =>
        AreEqual("now set", new Simulation().Also(LobFixture).ExecuteScalar("""
            update t set tx = '' where id = 2;
            declare @p varbinary(16);
            select @p = textptr(tx) from t where id = 2;
            writetext t.tx @p 'now set';
            select cast(tx as varchar(50)) from t where id = 2;
            """));

    /// <summary>
    /// <c>UPDATETEXT</c>'s splice, with NULL (or a negative value) meaning
    /// append for the offset and to-the-end for the deletion length.
    /// </summary>
    [TestMethod]
    [DataRow("updatetext t.tx @p 2 0 'XY'", "01XY23456789")]
    [DataRow("updatetext t.tx @p 0 3 'ABC'", "ABC3456789")]
    [DataRow("updatetext t.tx @p 0 2", "23456789")]
    [DataRow("updatetext t.tx @p null 0 '<APP>'", "0123456789<APP>")]
    [DataRow("updatetext t.tx @p -1 0 '<APP>'", "0123456789<APP>")]
    [DataRow("updatetext t.tx @p 3 null '#'", "012#")]
    [DataRow("updatetext t.tx @p 3 -1 '#'", "012#")]
    [DataRow("updatetext t.tx @p 1 0", "0123456789")]
    [DataRow("updatetext t.tx @p 10 0 '!END'", "0123456789!END")]
    [DataRow("updatetext t.tx @p 0 1 with log 'Q'", "Q123456789")]
    public void UpdateText_Splices(string statement, string expected) =>
        AreEqual(expected, WithPointer(
            "tx",
            1,
            $"{statement}; select cast(tx as varchar(50)) from t where id = 1",
            "create table t (id int primary key, tx text); insert t values (1, '0123456789');"));

    [TestMethod]
    public void UpdateText_Ntext_CountsCharacters() =>
        AreEqual("ABÜDEFGHIJ", WithPointer(
            "nt",
            1,
            "updatetext t.nt @p 2 1 N'Ü'; select cast(nt as nvarchar(50)) from t where id = 1",
            "create table t (id int primary key, nt ntext); insert t values (1, N'ABCDEFGHIJ');"));

    [TestMethod]
    public void UpdateText_Image_CountsBytes() =>
        AreEqual("0011FFEE445566778899", Convert.ToHexString((byte[])WithPointer(
            "im",
            1,
            "updatetext t.im @p 2 2 0xFFEE; select im from t where id = 1",
            "create table t (id int primary key, im image); insert t values (1, 0x00112233445566778899);")!));

    /// <summary>The copy form takes its inserted data from a second LOB cell.</summary>
    [TestMethod]
    public void UpdateText_CopyForm_SplicesSourceValue() =>
        AreEqual("01SOURCE56789", new Simulation().Also("create table t (id int primary key, tx text, src text); insert t values (1, '0123456789', 'SOURCE');").ExecuteScalar("""
            declare @p varbinary(16), @q varbinary(16);
            select @p = textptr(tx), @q = textptr(src) from t where id = 1;
            updatetext t.tx @p 2 3 t.src @q;
            select cast(tx as varchar(50)) from t where id = 1;
            """));

    [TestMethod]
    public void UpdateText_CopyForm_TypeMismatch_RaisesMsg518()
    {
        var simulation = new Simulation().Also("create table t (id int primary key, tx text, nt ntext); insert t values (1, '0123456789', N'SRC');");
        simulation.AssertSqlError(
            """
            declare @p varbinary(16), @q varbinary(16);
            select @p = textptr(tx), @q = textptr(nt) from t where id = 1;
            updatetext t.tx @p 0 0 t.nt @q;
            """,
            518,
            "Cannot convert data type ntext to text.");
    }

    [TestMethod]
    public void UpdateText_OffsetPastValue_RaisesMsg7116()
    {
        var ex = Throws<SimulatedSqlException>(() => WithPointer("tx", 1, "updatetext t.tx @p 100 0 'z'"));
        AreEqual(7116, ex.Number);
        AreEqual((byte)4, ex.State);
        AreEqual("Offset 100 is not in the range of available LOB data.", ex.Message);
    }

    [TestMethod]
    public void UpdateText_DeletionPastValue_RaisesMsg7135()
    {
        var ex = Throws<SimulatedSqlException>(() => WithPointer("tx", 1, "updatetext t.tx @p 1 500 'z'"));
        AreEqual(7135, ex.Number);
        AreEqual((byte)4, ex.State);
        AreEqual("Deletion length 500 is not in the range of available text, ntext, or image data.", ex.Message);
    }

    /// <summary>
    /// One pointer drives a write and then a run of appends — the idiom the
    /// statements exist for, which needs the pointer to survive its own writes.
    /// </summary>
    [TestMethod]
    public void ChunkedWrite_OnePointerAcrossWrites() =>
        AreEqual("chunk1chunk2chunk3", new Simulation().Also("create table t (id int primary key, tx text); insert t values (1, 'seed');").ExecuteScalar("""
            declare @p varbinary(16);
            select @p = textptr(tx) from t where id = 1;
            writetext t.tx @p 'chunk1';
            updatetext t.tx @p null 0 'chunk2';
            updatetext t.tx @p null 0 'chunk3';
            select cast(tx as varchar(100)) from t where id = 1;
            """));

    /// <summary>
    /// Bytes that aren't a pointer this column answers to — arbitrary bytes, a
    /// pointer read from another column, and one whose row has since been
    /// deleted — are all Msg 7123 rendering the value real's way.
    /// </summary>
    [TestMethod]
    public void BogusPointer_RaisesMsg7123()
    {
        var simulation = new Simulation().Also(LobFixture);
        var ex = simulation.AssertSqlError("declare @p varbinary(16) = 0x11111111111111111111111111111111; readtext t.tx @p 0 5", 7123);
        AreEqual("Invalid text, ntext, or image pointer value 0x11111111111111111111111111111111.", ex.Message);
    }

    [TestMethod]
    public void PointerOfAnotherColumn_RaisesMsg7123() =>
        AreEqual(7123, Throws<SimulatedSqlException>(() => WithPointer("nt", 1, "readtext t.tx @p 0 5")).Number);

    [TestMethod]
    public void PointerOfDeletedRow_RaisesMsg7123() =>
        _ = new Simulation().Also(LobFixture).AssertSqlError("""
            declare @p varbinary(16);
            select @p = textptr(tx) from t where id = 1;
            delete t where id = 1;
            readtext t.tx @p 0 5;
            """, 7123);

    [TestMethod]
    public void ShortPointer_RaisesMsg7122() =>
        new Simulation().Also(LobFixture).AssertSqlError(
            "declare @p varbinary(16) = 0x00; readtext t.tx @p 0 5",
            7122,
            "Invalid text, ntext, or image pointer type. Must be binary(16).");

    [TestMethod]
    public void NonLobColumn_RaisesMsg7125() =>
        new Simulation().Also(LobFixture).AssertSqlError(
            "declare @p varbinary(16) = 0x00; readtext t.id @p 0 5",
            7125,
            "The text, ntext, or image pointer value conflicts with the column name specified.");

    [TestMethod]
    public void SinglePartName_RaisesMsg182() =>
        new Simulation().Also(LobFixture).AssertSqlError(
            "declare @p varbinary(16) = 0x00; readtext tx @p 0 5",
            182,
            "Table and column names must be supplied for the READTEXT or WRITETEXT utility.");

    [TestMethod]
    [DataRow("readtext dbo.nosuch.tx @p 0 5", 208)]
    [DataRow("readtext t.nosuchcol @p 0 5", 207)]
    public void UnknownTargetName_Raises(string statement, int number) =>
        _ = new Simulation().Also(LobFixture).AssertSqlError($"declare @p varbinary(16) = 0x00; {statement}", number);

    /// <summary>
    /// The three statements report the row counts real reports:
    /// <c>WRITETEXT</c> 0, <c>UPDATETEXT</c> 1, <c>READTEXT</c> 1.
    /// </summary>
    [TestMethod]
    [DataRow("writetext t.tx @p 'x'", 0)]
    [DataRow("updatetext t.tx @p 0 1 'x'", 1)]
    [DataRow("readtext t.tx @p 0 1", 1)]
    public void RowCount_MatchesStatement(string statement, int expected)
    {
        var simulation = new Simulation().Also(LobFixture);
        using var reader = simulation.ExecuteReader($"""
            declare @p varbinary(16);
            select @p = textptr(tx) from t where id = 1;
            {statement};
            select @@rowcount as rc;
            """);
        while (reader.GetName(0) != "rc")
            IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(expected, reader.GetInt32(0));
    }

    /// <summary>
    /// Neither write is DML as far as triggers are concerned — real's AFTER
    /// UPDATE trigger stays silent for both (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("writetext t.tx @p 'x'")]
    [DataRow("updatetext t.tx @p 0 1 'x'")]
    public void Writes_DoNotFireTriggers(string statement)
    {
        var simulation = new Simulation().Also("""
            create table t (id int primary key, tx text);
            create table log (n int);
            insert t values (1, 'seed');
            """);
        _ = simulation.ExecuteNonQuery("create trigger t_u on t after update as insert log values (1)");
        _ = simulation.ExecuteScalar($"declare @p varbinary(16); select @p = textptr(tx) from t where id = 1; {statement};");
        AreEqual(0, simulation.ExecuteScalar<int>("select count(*) from log"));
    }

    /// <summary>Both writes participate in the transaction that wrapped them.</summary>
    [TestMethod]
    [DataRow("writetext t.tx @p 'written'")]
    [DataRow("updatetext t.tx @p 0 4 'written'")]
    public void Writes_RollBackWithTheirTransaction(string statement)
    {
        var simulation = new Simulation().Also("create table t (id int primary key, tx text); insert t values (1, 'seed');");
        _ = simulation.ExecuteNonQuery($"""
            begin tran;
            declare @p varbinary(16);
            select @p = textptr(tx) from t where id = 1;
            {statement};
            rollback;
            """);
        AreEqual("seed", simulation.ExecuteScalar("select cast(tx as varchar(50)) from t where id = 1"));
    }

    /// <summary>
    /// The result set carries the column's own name and type, so a
    /// <c>SET TEXTSIZE</c> caps it at the client boundary like any other LOB
    /// read (probe-confirmed: <c>TEXTSIZE 4</c> leaves 4 bytes of <c>text</c>).
    /// </summary>
    [TestMethod]
    public void ReadText_ResultCarriesColumnIdentityAndHonoursTextSize()
    {
        var simulation = new Simulation().Also(LobFixture);
        using var reader = simulation.ExecuteReader("""
            set textsize 4;
            declare @p varbinary(16);
            select @p = textptr(tx) from t where id = 1;
            readtext t.tx @p 0 10;
            """);
        AreEqual("tx", reader.GetName(0));
        AreEqual("text", reader.GetDataTypeName(0));
        IsTrue(reader.Read());
        AreEqual("Hell", reader.GetString(0));
    }
}

internal static class LegacyLobTestExtensions
{
    /// <summary>Runs setup against a fresh simulation and hands it back.</summary>
    public static Simulation Also(this Simulation simulation, string setup)
    {
        _ = simulation.ExecuteNonQuery(setup);
        return simulation;
    }
}
