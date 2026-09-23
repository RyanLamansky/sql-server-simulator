namespace SqlServerSimulator.Storage;

// Type-pair legality: whether two operand types may meet in a unification, a
// comparison or an arithmetic operator, and the error real raises when not.
partial class SqlType
{
    private const int PairClassCount = (int)TypePairClass.Spatial + 1;

    // The grids: one row per left operand class, one column per right operand
    // class, both in TypePairClass order. A cell is the outcome real reports:
    //   .     legal
    //   C  c  Msg 206 operand type clash — C names the left operand first, c the right
    //   V  v  Msg 257 (Msg 260 for a column) — V converts the left operand to the
    //         right's type, v the right to the left's
    //   I     Msg 402 incompatible in the operator, operands in written order
    //   O     Msg 8117 operand data type invalid, naming the left operand
    //   U  u  Msg 403 invalid operator, naming the left / right operand
    //   X     Msg 305, xml can't be compared
    // Probed 2026-09-23 against SQL Server 2025 over every ordered pair of 37
    // types (every class member, the MAX forms and sysname included), each one
    // statement per batch over empty-table columns so constant folding can't
    // intervene; every member of a class answered identically. The single
    // exception is folded in by PairError: a MAX string or binary can't reach
    // sql_variant, so that pair is Msg 206 naming the MAX side first.
    //
    // Each grid is one span, row after row, so a row that isn't exactly
    // PairClassCount cells long shifts every cell after it; the lookup
    // asserts the total.
    //
    // Columns:                    bit int exact approx ansi uni bin text image ts uid dt date time dt2 xml variant hid spatial

    private static ReadOnlySpan<byte> UnifyGrid =>
        ".......cc.c.CCCC.CC"u8 // bit
        + ".......cc.c.CCCC.CC"u8 // integer
        + ".......cc.c.CCCC.CC"u8 // exact numeric
        + "......ccccc.CCCC.CC"u8 // approximate
        + ".........V........."u8 // ansi string
        + "........CV........."u8 // unicode string
        + "...C...C....VVV...."u8 // binary
        + "CCCC..c.cccCCCC.CCC"u8 // text
        + "CCCC.c.C..cCCCCCCCC"u8 // image
        + "...Cvv.C..c.CCCCCCC"u8 // timestamp
        + "CCCC...CCC.CCCCC.CC"u8 // uniqueidentifier
        + ".......cc.c....C.CC"u8 // datetime / smalldatetime
        + "cccc..vcccc..c.C.CC"u8 // date
        + "cccc..vcccc.C..C.CC"u8 // time
        + "cccc..vcccc....C.CC"u8 // datetime2 / datetimeoffset
        + "cccc....ccccccc.ccc"u8 // xml
        + ".......ccc.....C.CC"u8 // sql_variant
        + "cccc...ccccccccCc.c"u8 // hierarchyid
        + "cccc...ccccccccCccc"u8; // spatial

    private static ReadOnlySpan<byte> CompareGrid =>
        ".......cc.c.cccc.Cu"u8 // bit
        + ".......cc.c.cccc.Cu"u8 // integer
        + ".......cc.c.cccc.Cu"u8 // exact numeric
        + "......ccccc.cccc.Cu"u8 // approximate
        + ".......IIV.....I..u"u8 // ansi string
        + ".......IIV.....I..u"u8 // unicode string
        + "...C...II...IIII..u"u8 // binary
        + "CCCCIIIIICCCIIIICCu"u8 // text
        + "CCCCIIIII.CCIIIICCu"u8 // image
        + "...Cvv.c..c.ccccCCu"u8 // timestamp
        + "CCCC...ccC.Ccccc.Cu"u8 // uniqueidentifier
        + ".......cc.c..I.c.Cu"u8 // datetime / smalldatetime
        + "CCCC..IIICC..I.I.Cu"u8 // date
        + "CCCC..IIICCII.II.Cu"u8 // time
        + "CCCC..IIICC..I.I.Cu"u8 // datetime2 / datetimeoffset
        + "CCCCIIIIICCCIIIXCCu"u8 // xml
        + ".......ccc.....c.Cu"u8 // sql_variant
        + "cccc...cccccccccc.c"u8 // hierarchyid
        + "UUUUUUUUUUUUUUUUUUU"u8; // spatial

    private static ReadOnlySpan<byte> AddGrid =>
        "I...IIIIIII.IIIIIuu"u8 // bit
        + ".......cc.c.ccccvuu"u8 // integer
        + ".......cc.c.ccccvuu"u8 // exact numeric
        + "......ccccc.ccccvuu"u8 // approximate
        + "I.....IIIII.IIIIIuu"u8 // ansi string
        + "I.....IIIII.IIIIIuu"u8 // unicode string
        + "I..CII.II.I.IIIIIuu"u8 // binary
        + "ICCCIIIOOIOIOOOOIuu"u8 // text
        + "ICCCIIIOOIOIOOOOIuu"u8 // image
        + "I..CII.II.IIIIIIIuu"u8 // timestamp
        + "ICCCIIIOOIOIOOOOIuu"u8 // uniqueidentifier
        + ".......IIII.IIIIvuu"u8 // datetime / smalldatetime
        + "ICCCIIIOOIOIOOOOIuu"u8 // date
        + "ICCCIIIOOIOIOOOOIuu"u8 // time
        + "ICCCIIIOOIOIOOOOIuu"u8 // datetime2 / datetimeoffset
        + "ICCCIIIOOIOIOOOOIuu"u8 // xml
        + "IVVVIIIIIIIVIIIIIuu"u8 // sql_variant
        + "UUUUUUUUUUUUUUUUUUU"u8 // hierarchyid
        + "UUUUUUUUUUUUUUUUUUU"u8; // spatial

    private static ReadOnlySpan<byte> SubtractGrid =>
        "I...IIIIIII.IIIIIuu"u8 // bit
        + ".......cc.c.ccccvuu"u8 // integer
        + ".......cc.c.ccccvuu"u8 // exact numeric
        + "......ccccc.ccccvuu"u8 // approximate
        + "I...IIIIIII.IIIIIuu"u8 // ansi string
        + "I...IIIIIII.IIIIIuu"u8 // unicode string
        + "I..CIIIIIII.IIIIIuu"u8 // binary
        + "ICCCIIIOOOOIOOOOIuu"u8 // text
        + "ICCCIIIOOOOIOOOOIuu"u8 // image
        + "I..CIIIOOOOIOOOOIuu"u8 // timestamp
        + "ICCCIIIOOOOIOOOOIuu"u8 // uniqueidentifier
        + ".......IIII.IIIIvuu"u8 // datetime / smalldatetime
        + "ICCCIIIOOOOIOOOOIuu"u8 // date
        + "ICCCIIIOOOOIOOOOIuu"u8 // time
        + "ICCCIIIOOOOIOOOOIuu"u8 // datetime2 / datetimeoffset
        + "ICCCIIIOOOOIOOOOIuu"u8 // xml
        + "IVVVIIIIIIIVIIIIIuu"u8 // sql_variant
        + "UUUUUUUUUUUUUUUUUUU"u8 // hierarchyid
        + "UUUUUUUUUUUUUUUUUUU"u8; // spatial

    private static ReadOnlySpan<byte> MultiplyDivideGrid =>
        "O...OOOOOOOOOOOOOuu"u8 // bit
        + ".......cc.cvccccvuu"u8 // integer
        + ".......cc.cvccccvuu"u8 // exact numeric
        + "......cccccvccccvuu"u8 // approximate
        + "O...OOOOOOOOOOOOOuu"u8 // ansi string
        + "O...OOOOOOOOOOOOOuu"u8 // unicode string
        + "O..COOOOOOOOOOOOOuu"u8 // binary
        + "OCCCOOOOOOOOOOOOOuu"u8 // text
        + "OCCCOOOOOOOOOOOOOuu"u8 // image
        + "O..COOOOOOOOOOOOOuu"u8 // timestamp
        + "OCCCOOOOOOOOOOOOOuu"u8 // uniqueidentifier
        + "OVVVOOOOOOOOOOOOOuu"u8 // datetime / smalldatetime
        + "OCCCOOOOOOOOOOOOOuu"u8 // date
        + "OCCCOOOOOOOOOOOOOuu"u8 // time
        + "OCCCOOOOOOOOOOOOOuu"u8 // datetime2 / datetimeoffset
        + "OCCCOOOOOOOOOOOOOuu"u8 // xml
        + "OVVVOOOOOOOOOOOOOuu"u8 // sql_variant
        + "UUUUUUUUUUUUUUUUUUU"u8 // hierarchyid
        + "UUUUUUUUUUUUUUUUUUU"u8; // spatial

    private static ReadOnlySpan<byte> ModuloGrid =>
        "I..IIIIIIIIIIIIIIuu"u8 // bit
        + "...I...II.IIIIIIIuu"u8 // integer
        + "...IIIIIIIIIIIIIIuu"u8 // exact numeric
        + "IIIOIIIOOIOOOOOOOuu"u8 // approximate
        + "I.IIIIIIIIIIIIIIIuu"u8 // ansi string
        + "I.IIIIIIIIIIIIIIIuu"u8 // unicode string
        + "I.IIIIIIIIIIIIIIIuu"u8 // binary
        + "IIIOIIIOOIOOOOOOOuu"u8 // text
        + "IIIOIIIOOIOOOOOOOuu"u8 // image
        + "I.IIIIIIIIIIIIIIIuu"u8 // timestamp
        + "IIIOIIIOOIOOOOOOOuu"u8 // uniqueidentifier
        + "IIIOIIIOOIOOOOOOOuu"u8 // datetime / smalldatetime
        + "IIIOIIIOOIOOOOOOOuu"u8 // date
        + "IIIOIIIOOIOOOOOOOuu"u8 // time
        + "IIIOIIIOOIOOOOOOOuu"u8 // datetime2 / datetimeoffset
        + "IIIOIIIOOIOOOOOOOuu"u8 // xml
        + "IIIOIIIOOIOOOOOOOuu"u8 // sql_variant
        + "UUUUUUUUUUUUUUUUUUU"u8 // hierarchyid
        + "UUUUUUUUUUUUUUUUUUU"u8; // spatial

    /// <summary>
    /// Whether <paramref name="left"/> and <paramref name="right"/> may meet in
    /// <paramref name="operation"/>: <see langword="null"/> when real accepts
    /// the pair, else the exception real raises for it. Legality is a
    /// property of the two types alone, so callers ask while compiling and a
    /// typed NULL or an empty rowset raises exactly as a value would.
    /// </summary>
    /// <param name="operation">Which grid to read.</param>
    /// <param name="left">The left operand, as written.</param>
    /// <param name="right">The right operand, as written.</param>
    /// <param name="operatorName">The operator as real's Msg 402 / 403 / 8117
    /// spell it (<c>equal to</c>, <c>add</c>, <c>modulo</c>); a unification
    /// raises none of those, so it may pass anything.</param>
    public static SimulatedSqlException? OperandPairError(TypePairOperation operation, TypePairOperand left, TypePairOperand right, string operatorName)
    {
        var leftType = left.Type;
        var rightType = right.Type;
        if (operation == TypePairOperation.Unify && leftType == rightType)
            return null;

        if (operation is TypePairOperation.Unify or TypePairOperation.Compare
            && (IsMaxLengthVariantPair(leftType, rightType) || IsMaxLengthVariantPair(rightType, leftType)))
        {
            return leftType is SqlVariantSqlType
                ? SimulatedSqlException.OperandTypeClash(OperandName(right), OperandName(left))
                : SimulatedSqlException.OperandTypeClash(OperandName(left), OperandName(right));
        }

        var grid = operation switch
        {
            TypePairOperation.Unify => UnifyGrid,
            TypePairOperation.Compare => CompareGrid,
            TypePairOperation.Add => AddGrid,
            TypePairOperation.Subtract => SubtractGrid,
            TypePairOperation.Modulo => ModuloGrid,
            _ => MultiplyDivideGrid,
        };
        System.Diagnostics.Debug.Assert(grid.Length == PairClassCount * PairClassCount, "A type-pair grid row has the wrong length.");
        return (char)grid[((int)leftType.PairClass * PairClassCount) + (int)rightType.PairClass] switch
        {
            '.' => null,
            'C' => SimulatedSqlException.OperandTypeClash(OperandName(left), OperandName(right)),
            'c' => SimulatedSqlException.OperandTypeClash(OperandName(right), OperandName(left)),
            'I' => SimulatedSqlException.IncompatibleDataTypesInOperator(OperandName(left), OperandName(right), operatorName),
            'O' => SimulatedSqlException.OperandDataTypeInvalid(OperandName(left), operatorName),
            'U' => SimulatedSqlException.InvalidOperatorForDataType(operatorName, OperandName(left)),
            'u' => SimulatedSqlException.InvalidOperatorForDataType(operatorName, OperandName(right)),
            'V' => ImplicitConversionError(operation, left, right),
            'v' => ImplicitConversionError(operation, right, left),
            _ => SimulatedSqlException.XmlCannotBeComparedOrSorted(),
        };
    }

    /// <summary>
    /// The type-only form of <see cref="OperandPairError"/>, for callers
    /// holding no expression to name an operand by.
    /// </summary>
    public static SimulatedSqlException? PairError(TypePairOperation operation, SqlType left, SqlType right, string operatorName) =>
        OperandPairError(operation, new TypePairOperand(left), new TypePairOperand(right), operatorName);

    private static bool IsMaxLengthVariantPair(SqlType maxSide, SqlType variantSide) =>
        variantSide is SqlVariantSqlType
        && maxSide is VarcharSqlType { length: MaxLengthSentinel } or NVarcharSqlType { length: MaxLengthSentinel } or VarbinarySqlType { length: MaxLengthSentinel };

    /// <summary>
    /// Msg 257, or Msg 260 when the converted operand is a column: real names
    /// the column's object there, but only for an operator — a unification's
    /// refusal is Msg 257 whatever its branches are.
    /// </summary>
    private static SimulatedSqlException ImplicitConversionError(TypePairOperation operation, TypePairOperand source, TypePairOperand target) =>
        operation != TypePairOperation.Unify && source.SourceTable is { } table && source.SourceColumn is { } column
            ? SimulatedSqlException.DisallowedImplicitConversionFromColumn(OperandName(source), OperandName(target), table, column)
            : SimulatedSqlException.ImplicitConversionNotAllowed(OperandName(source), OperandName(target));

    /// <summary>
    /// The type name real's pair diagnostics print: the bare family root, a
    /// MAX form keeping its <c>(max)</c>, <c>sysname</c> as the
    /// <c>nvarchar</c> it aliases, and a <c>decimal</c> written as
    /// <c>numeric</c> keeping that spelling.
    /// </summary>
    private static string OperandName(TypePairOperand operand) =>
        operand.Type is SystemNameSqlType ? "nvarchar"
        : operand.Type is DecimalSqlType && operand.Source is { ResultReportsNumeric: true } ? "numeric"
        : SimulatedSqlException.FamilyRootName(operand.Type);
}
