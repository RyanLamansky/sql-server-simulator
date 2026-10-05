namespace SqlServerSimulator.Storage;

// Type-pair legality: whether two operand types may meet in a unification, a
// comparison or an arithmetic operator, and the error real raises when not.
partial class SqlType
{
    private const int PairClassCount = (int)TypePairClass.Json + 1;

    // The grids: one row per left operand class, one column per right operand
    // class, both in TypePairClass order. A cell is the outcome real reports:
    //   .     legal
    //   C  c  Msg 206 operand type clash — C names the left operand first, c the right
    //   V  v  Msg 257 (Msg 260 for a column) — V converts the left operand to the
    //         right's type, v the right to the left's
    //   I     Msg 402 incompatible in the operator, operands in written order
    //   O  o  Msg 8117 operand data type invalid, naming the left / right operand
    //   U  u  Msg 403 invalid operator, naming the left / right operand
    //   X     Msg 305, xml can't be compared
    //   J     Msg 13636, json can't be compared
    // Probed 2026-09-23 against SQL Server 2025 over every ordered pair of 37
    // types (every class member, the MAX forms and sysname included), each one
    // statement per batch over empty-table columns so constant folding can't
    // intervene; every member of a class answered identically. The single
    // exception is folded in by PairError: a MAX string or binary can't reach
    // sql_variant, so that pair is Msg 206 naming the MAX side first. The
    // vector row and column were probed 2026-09-26 the same way, vector(2)
    // against one member of each class in both orders, and the json row and
    // column the same way on 2026-09-26.
    //
    // Each grid is one span, row after row, so a row that isn't exactly
    // PairClassCount cells long shifts every cell after it; the lookup
    // asserts the total.
    //
    // Columns:                    bit int exact approx ansi uni bin text image ts uid dt date time dt2 xml variant hid spatial vector json

    // Assign reads its source down the rows and its target across the
    // columns, probed 2026-09-24 against SQL Server 2025 by declaring a
    // variable of every type initialized from one of every other; the text /
    // image classes and a timestamp target were left out (no variable takes
    // them) and read as legal.
    private static ReadOnlySpan<byte> AssignGrid =>
        "..........C.CCCC.CCCC"u8 // bit
        + "..........C.CCCC.CCCC"u8 // integer
        + "..........C.CCCC.CCCC"u8 // exact numeric
        + ".........CC.CCCC.CCCC"u8 // approximate
        + "......V..V..........."u8 // ansi string
        + "......V..V..........."u8 // unicode string
        + "...C........VVV....CC"u8 // binary
        + ".........C..........."u8 // text
        + "....................."u8 // image
        + "...C.C....C.CCCCCCC.."u8 // timestamp
        + "CCCC.....C.CCCCC.CCCC"u8 // uniqueidentifier
        + "VVVV..V..VC....C.CCCC"u8 // datetime / smalldatetime
        + "CCCC..V..CC..C.C.CCCC"u8 // date
        + "CCCC..V..CC.C..C.CCCC"u8 // time
        + "CCCC..V..CC....C.CCCC"u8 // datetime2 / datetimeoffset
        + "CCCCVVV..CCCCCC.CCCCC"u8 // xml
        + "VVVVVVV..CVVVVVC.CCCC"u8 // sql_variant
        + "CCCCVVV..CCCCCCCC.CCC"u8 // hierarchyid
        + "CCCCVVV..CCCCCCCCC.CC"u8 // spatial
        + "CCCC..C..CCCCCCCCCC.."u8 // vector
        + "CCCCVVC..CCCCCCCCCC.."u8; // json

    private static ReadOnlySpan<byte> UnifyGrid =>
        ".......cc.c.CCCC.CCcC"u8 // bit
        + ".......cc.c.CCCC.CCcC"u8 // integer
        + ".......cc.c.CCCC.CCcC"u8 // exact numeric
        + "......ccccc.CCCC.CCcC"u8 // approximate
        + ".........V..........."u8 // ansi string
        + "........CV..........."u8 // unicode string
        + "...C...C....VVV....CC"u8 // binary
        + "CCCC..c.cccCCCC.CCCcC"u8 // text
        + "CCCC.c.C..cCCCCCCCCcC"u8 // image
        + "...Cvv.C..c.CCCCCCCcC"u8 // timestamp
        + "CCCC...CCC.CCCCC.CCcC"u8 // uniqueidentifier
        + ".......cc.c....C.CCcC"u8 // datetime / smalldatetime
        + "cccc..vcccc..c.C.CCcC"u8 // date
        + "cccc..vcccc.C..C.CCcC"u8 // time
        + "cccc..vcccc....C.CCcC"u8 // datetime2 / datetimeoffset
        + "cccc....ccccccc.ccccC"u8 // xml
        + ".......ccc.....C.CCcC"u8 // sql_variant
        + "cccc...ccccccccCc.ccC"u8 // hierarchyid
        + "cccc...ccccccccCccccC"u8 // spatial
        + "CCCC..cCCCCCCCCCCCC.."u8 // vector
        + "cccc..ccccccccccccc.."u8; // json

    private static ReadOnlySpan<byte> CompareGrid =>
        ".......cc.c.cccc.CuIc"u8 // bit
        + ".......cc.c.cccc.CuIc"u8 // integer
        + ".......cc.c.cccc.CuIc"u8 // exact numeric
        + "......ccccc.cccc.Cuoc"u8 // approximate
        + ".......IIV.....I..uoI"u8 // ansi string
        + ".......IIV.....I..uoI"u8 // unicode string
        + "...C...II...IIII..uoI"u8 // binary
        + "CCCCIIIIICCCIIIICCuoI"u8 // text
        + "CCCCIIIII.CCIIIICCuoI"u8 // image
        + "...Cvv.c..c.ccccCCuoc"u8 // timestamp
        + "CCCC...ccC.Ccccc.Cuoc"u8 // uniqueidentifier
        + ".......cc.c..I.c.CuoI"u8 // datetime / smalldatetime
        + "CCCC..IIICC..I.I.CuoI"u8 // date
        + "CCCC..IIICCII.II.CuoI"u8 // time
        + "CCCC..IIICC..I.I.CuoI"u8 // datetime2 / datetimeoffset
        + "CCCCIIIIICCCIIIXCCuoI"u8 // xml
        + ".......ccc.....c.Cuoc"u8 // sql_variant
        + "cccc...cccccccccc.ccc"u8 // hierarchyid
        + "UUUUUUUUUUUUUUUUUUUUU"u8 // spatial
        + "IIIOOOOOOOOOOOOOOCuOO"u8 // vector
        + "CCCCIIIIICCIIIIICCuoJ"u8; // json

    private static ReadOnlySpan<byte> AddGrid =>
        "I...IIIIIII.IIIIIuuoI"u8 // bit
        + ".......cc.c.ccccvuuoc"u8 // integer
        + ".......cc.c.ccccvuuoc"u8 // exact numeric
        + "......ccccc.ccccvuuoc"u8 // approximate
        + "I.....IIIII.IIIIIuuoI"u8 // ansi string
        + "I.....IIIII.IIIIIuuoI"u8 // unicode string
        + "I..CII.II.I.IIIIIuuoI"u8 // binary
        + "ICCCIIIOOIOIOOOOIuuoO"u8 // text
        + "ICCCIIIOOIOIOOOOIuuoO"u8 // image
        + "I..CII.II.IIIIIIIuuII"u8 // timestamp
        + "ICCCIIIOOIOIOOOOIuuoO"u8 // uniqueidentifier
        + ".......IIII.IIIIvuuoI"u8 // datetime / smalldatetime
        + "ICCCIIIOOIOIOOOOIuuoO"u8 // date
        + "ICCCIIIOOIOIOOOOIuuoO"u8 // time
        + "ICCCIIIOOIOIOOOOIuuoO"u8 // datetime2 / datetimeoffset
        + "ICCCIIIOOIOIOOOOIuuoO"u8 // xml
        + "IVVVIIIIIIIVIIIIIuuoI"u8 // sql_variant
        + "UUUUUUUUUUUUUUUUUUUUU"u8 // hierarchyid
        + "UUUUUUUUUUUUUUUUUUUUU"u8 // spatial
        + "OOOOOOOOOIOOOOOOOuuOO"u8 // vector
        + "ICCCIIIOOIOIOOOOIuuoO"u8; // json

    private static ReadOnlySpan<byte> SubtractGrid =>
        "I...IIIIIII.IIIIIuuoI"u8 // bit
        + ".......cc.c.ccccvuuoc"u8 // integer
        + ".......cc.c.ccccvuuoc"u8 // exact numeric
        + "......ccccc.ccccvuuoc"u8 // approximate
        + "I...IIIIIII.IIIIIuuoI"u8 // ansi string
        + "I...IIIIIII.IIIIIuuoI"u8 // unicode string
        + "I..CIIIIIII.IIIIIuuoI"u8 // binary
        + "ICCCIIIOOOOIOOOOIuuoO"u8 // text
        + "ICCCIIIOOOOIOOOOIuuoO"u8 // image
        + "I..CIIIOOOOIOOOOIuuoO"u8 // timestamp
        + "ICCCIIIOOOOIOOOOIuuoO"u8 // uniqueidentifier
        + ".......IIII.IIIIvuuoI"u8 // datetime / smalldatetime
        + "ICCCIIIOOOOIOOOOIuuoO"u8 // date
        + "ICCCIIIOOOOIOOOOIuuoO"u8 // time
        + "ICCCIIIOOOOIOOOOIuuoO"u8 // datetime2 / datetimeoffset
        + "ICCCIIIOOOOIOOOOIuuoO"u8 // xml
        + "IVVVIIIIIIIVIIIIIuuoI"u8 // sql_variant
        + "UUUUUUUUUUUUUUUUUUUUU"u8 // hierarchyid
        + "UUUUUUUUUUUUUUUUUUUUU"u8 // spatial
        + "OOOOOOOOOOOOOOOOOuuOO"u8 // vector
        + "ICCCIIIOOOOIOOOOIuuoO"u8; // json

    private static ReadOnlySpan<byte> MultiplyDivideGrid =>
        "O...OOOOOOOOOOOOOuuoO"u8 // bit
        + ".......cc.cvccccvuuoc"u8 // integer
        + ".......cc.cvccccvuuoc"u8 // exact numeric
        + "......cccccvccccvuuoc"u8 // approximate
        + "O...OOOOOOOOOOOOOuuoO"u8 // ansi string
        + "O...OOOOOOOOOOOOOuuoO"u8 // unicode string
        + "O..COOOOOOOOOOOOOuuoO"u8 // binary
        + "OCCCOOOOOOOOOOOOOuuoO"u8 // text
        + "OCCCOOOOOOOOOOOOOuuoO"u8 // image
        + "O..COOOOOOOOOOOOOuuoO"u8 // timestamp
        + "OCCCOOOOOOOOOOOOOuuoO"u8 // uniqueidentifier
        + "OVVVOOOOOOOOOOOOOuuoO"u8 // datetime / smalldatetime
        + "OCCCOOOOOOOOOOOOOuuoO"u8 // date
        + "OCCCOOOOOOOOOOOOOuuoO"u8 // time
        + "OCCCOOOOOOOOOOOOOuuoO"u8 // datetime2 / datetimeoffset
        + "OCCCOOOOOOOOOOOOOuuoO"u8 // xml
        + "OVVVOOOOOOOOOOOOOuuoO"u8 // sql_variant
        + "UUUUUUUUUUUUUUUUUUUUU"u8 // hierarchyid
        + "UUUUUUUUUUUUUUUUUUUUU"u8 // spatial
        + "OOOOOOOOOOOOOOOOOuuOO"u8 // vector
        + "OCCCOOOOOOOOOOOOOuuoO"u8; // json

    private static ReadOnlySpan<byte> ModuloGrid =>
        "I..IIIIIIIIIIIIIIuuoI"u8 // bit
        + "...I...II.IIIIIIIuuoI"u8 // integer
        + "...IIIIIIIIIIIIIIuuoI"u8 // exact numeric
        + "IIIOIIIOOIOOOOOOOuuoO"u8 // approximate
        + "I.IIIIIIIIIIIIIIIuuoI"u8 // ansi string
        + "I.IIIIIIIIIIIIIIIuuoI"u8 // unicode string
        + "I.IIIIIIIIIIIIIIIuuoI"u8 // binary
        + "IIIOIIIOOIOOOOOOOuuoO"u8 // text
        + "IIIOIIIOOIOOOOOOOuuoO"u8 // image
        + "I.IIIIIIIIIIIIIIIuuoI"u8 // timestamp
        + "IIIOIIIOOIOOOOOOOuuoO"u8 // uniqueidentifier
        + "IIIOIIIOOIOOOOOOOuuoO"u8 // datetime / smalldatetime
        + "IIIOIIIOOIOOOOOOOuuoO"u8 // date
        + "IIIOIIIOOIOOOOOOOuuoO"u8 // time
        + "IIIOIIIOOIOOOOOOOuuoO"u8 // datetime2 / datetimeoffset
        + "IIIOIIIOOIOOOOOOOuuoO"u8 // xml
        + "IIIOIIIOOIOOOOOOOuuoO"u8 // sql_variant
        + "UUUUUUUUUUUUUUUUUUUUU"u8 // hierarchyid
        + "UUUUUUUUUUUUUUUUUUUUU"u8 // spatial
        + "OOOOOOOOOOOOOOOOOuuOO"u8 // vector
        + "IIIOIIIOOIOOOOOOOuuoO"u8; // json

    // The bitwise operators, probed 2026-09-25 against SQL Server 2025 over
    // every ordered pair of empty-table columns of the 19 classes with `&`,
    // `|` and `^` spot-checked to match: an integer takes an integer, a
    // string, a binary or a timestamp; the refusals name the operator
    // quoted ('&'). An untyped NULL is handled ahead of the grid, pairing only
    // with an integer.
    private static ReadOnlySpan<byte> BitwiseGrid =>
        "..II...II.IIIIIIIuuoI"u8 // bit
        + "..II...II.IIIIIIIuuoI"u8 // integer
        + "IIOOIIIOOIOOOOOOOuuoO"u8 // exact numeric
        + "IIOOIIIOOIOOOOOOOuuoO"u8 // approximate
        + "..IIIIIIIIIIIIIIIuuoI"u8 // ansi string
        + "..IIIIIIIIIIIIIIIuuoI"u8 // unicode string
        + "..IIIIIIIIIIIIIIIuuoI"u8 // binary
        + "IIOOIIIOOIOOOOOOOuuoO"u8 // text
        + "IIOOIIIOOIOOOOOOOuuoO"u8 // image
        + "..IIIIIIIIIIIIIIIuuoI"u8 // timestamp
        + "IIOOIIIOOIOOOOOOOuuoO"u8 // uniqueidentifier
        + "IIOOIIIOOIOOOOOOOuuoO"u8 // datetime / smalldatetime
        + "IIOOIIIOOIOOOOOOOuuoO"u8 // date
        + "IIOOIIIOOIOOOOOOOuuoO"u8 // time
        + "IIOOIIIOOIOOOOOOOuuoO"u8 // datetime2 / datetimeoffset
        + "IIOOIIIOOIOOOOOOOuuoO"u8 // xml
        + "IIOOIIIOOIOOOOOOOuuoO"u8 // sql_variant
        + "UUUUUUUUUUUUUUUUUUUUU"u8 // hierarchyid
        + "UUUUUUUUUUUUUUUUUUUUU"u8 // spatial
        + "OOOOOOOOOOOOOOOOOuuOO"u8 // vector
        + "IIOOIIIOOIOOOOOOOuuoO"u8; // json

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
        // An undeclared parameter sp_describe_undeclared_parameters is typing
        // takes its type from this pair rather than being judged by it.
        if (Parser.UndeclaredParameterDeduction.Intercept(operation, left, right, operatorName))
            return null;
        if (operation == TypePairOperation.Compare)
        {
            left = NarrowedIntegerLiteral(left);
            right = NarrowedIntegerLiteral(right);
        }
        if (operation == TypePairOperation.Bitwise && BitwiseNullError(left, right, operatorName) is { } nullError)
            return nullError;
        if (operation is TypePairOperation.Add or TypePairOperation.Subtract or TypePairOperation.Multiply or TypePairOperation.Divide or TypePairOperation.Modulo
            && ArithmeticNullRow(operation, left, right) is { } nullRow)
        {
            return ArithmeticNullError(nullRow, left, right, operatorName);
        }
        var leftType = left.Type;
        var rightType = right.Type;
        if (operation == TypePairOperation.Unify && leftType == rightType)
            return null;

        // Two vectors unify and assign only at one dimension count and base
        // type; arms of both base types settle on float32, and an assignment
        // converts the source to the target's (probed 2026-09-26 and
        // 2026-09-29 against SQL Server 2025).
        if (operation is TypePairOperation.Unify or TypePairOperation.Assign && leftType is VectorSqlType leftVector && rightType is VectorSqlType rightVector)
        {
            return leftVector == rightVector ? null
                : leftVector.IsFloat16 != rightVector.IsFloat16
                    ? operation == TypePairOperation.Unify
                        ? SimulatedSqlException.VectorBaseTypeConversion("float16", "float32")
                        : SimulatedSqlException.VectorBaseTypeConversion(leftVector.BaseTypeName, rightVector.BaseTypeName)
                : SimulatedSqlException.VectorDimensionsMismatch(leftVector.dimensions, rightVector.dimensions, 1);
        }

        if ((leftType is ClrUdtSqlType || rightType is ClrUdtSqlType) && ClrUdtPairError(operation, left, right, operatorName) is { } udtError)
            return udtError;

        // The two spatial types share a class but convert into each other
        // nowhere: assigning one to the other is Msg 206 (probed 2026-10-05
        // against SQL Server 2025).
        if (operation == TypePairOperation.Assign
            && leftType is SpatialSqlType && rightType is SpatialSqlType && leftType != rightType)
        {
            return SimulatedSqlException.OperandTypeClash(OperandName(left), OperandName(right));
        }

        if ((operation is TypePairOperation.Unify or TypePairOperation.Compare
                && (IsMaxLengthVariantPair(leftType, rightType) || IsMaxLengthVariantPair(rightType, leftType)))
            || (operation == TypePairOperation.Assign && IsMaxLengthVariantPair(leftType, rightType)))
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
            TypePairOperation.Assign => AssignGrid,
            TypePairOperation.Bitwise => BitwiseGrid,
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
            'o' => SimulatedSqlException.OperandDataTypeInvalid(OperandName(right), operatorName),
            'U' => SimulatedSqlException.InvalidOperatorForDataType(operatorName, OperandName(left)),
            'u' => SimulatedSqlException.InvalidOperatorForDataType(operatorName, OperandName(right)),
            'V' => ImplicitConversionError(operation, left, right),
            'v' => ImplicitConversionError(operation, right, left),
            'J' => SimulatedSqlException.JsonCannotBeComparedOrSorted(1),
            _ => SimulatedSqlException.XmlCannotBeComparedOrSorted(),
        };
    }

    /// <summary>
    /// The type-only form of <see cref="OperandPairError"/>, for callers
    /// holding no expression to name an operand by.
    /// </summary>
    public static SimulatedSqlException? PairError(TypePairOperation operation, SqlType left, SqlType right, string operatorName) =>
        OperandPairError(operation, new TypePairOperand(left), new TypePairOperand(right), operatorName);

    // An untyped NULL is a class of its own to the arithmetic operators, one
    // row per operator and side read across the other operand's class
    // (probed 2026-09-25 against SQL Server 2025 over empty-table columns):
    // `.` legal, `I` Msg 402 naming NULL in its position, `U` Msg 403 naming
    // the other operand. Two NULLs always combine. `+` alone is asymmetric —
    // a timestamp takes a NULL on its left but not on its right.
    private static ReadOnlySpan<byte> NullAfterAdd => "I......IIII.IIIIIUUII"u8;
    private static ReadOnlySpan<byte> NullBeforeAdd => "I......II.I.IIIIIUUII"u8;
    private static ReadOnlySpan<byte> NullSubtract => "I...IIIIIII.IIIIIUUII"u8;
    private static ReadOnlySpan<byte> NullMultiplyDivide => "I...IIIIIIIIIIIIIUUII"u8;
    private static ReadOnlySpan<byte> NullModulo => "I..IIIIIIIIIIIIIIUUII"u8;

    /// <summary>
    /// The NULL row cell for an arithmetic pair holding exactly one untyped
    /// <c>NULL</c>, or <see langword="null"/> when neither or both operands
    /// are one (two NULLs combine; no NULL reads the ordinary grid).
    /// </summary>
    private static char? ArithmeticNullRow(TypePairOperation operation, TypePairOperand left, TypePairOperand right)
    {
        var leftNull = left.Source is Parser.Expressions.Value { IsUntypedNull: true };
        var rightNull = right.Source is Parser.Expressions.Value { IsUntypedNull: true };
        if (leftNull == rightNull)
            return leftNull ? '.' : null;
        var row = operation switch
        {
            TypePairOperation.Add => rightNull ? NullAfterAdd : NullBeforeAdd,
            TypePairOperation.Subtract => NullSubtract,
            TypePairOperation.Modulo => NullModulo,
            _ => NullMultiplyDivide,
        };
        return (char)row[(int)(leftNull ? right : left).Type.PairClass];
    }

    private static SimulatedSqlException? ArithmeticNullError(char cell, TypePairOperand left, TypePairOperand right, string operatorName)
    {
        var leftNull = left.Source is Parser.Expressions.Value { IsUntypedNull: true };
        return cell switch
        {
            '.' => null,
            'U' => SimulatedSqlException.InvalidOperatorForDataType(operatorName, OperandName(leftNull ? right : left)),
            _ => SimulatedSqlException.IncompatibleDataTypesInOperator(leftNull ? "NULL" : OperandName(left), leftNull ? OperandName(right) : "NULL", operatorName),
        };
    }

    /// <summary>
    /// A bitwise operator's refusal for an untyped <c>NULL</c> operand, which
    /// pairs only with an integer or <c>bit</c>: beside a hierarchyid or a
    /// spatial type it is Msg 403 naming that type, beside a vector Msg 8117
    /// naming the vector (probed 2026-09-26), and beside anything else —
    /// another NULL included — Msg 402 naming it <c>NULL</c> (probed
    /// 2026-09-25 against SQL Server 2025). Null when neither operand is one,
    /// or the pairing is legal.
    /// </summary>
    private static SimulatedSqlException? BitwiseNullError(TypePairOperand left, TypePairOperand right, string operatorName)
    {
        var leftNull = left.Source is Parser.Expressions.Value { IsUntypedNull: true };
        var rightNull = right.Source is Parser.Expressions.Value { IsUntypedNull: true };
        if (!leftNull && !rightNull)
            return null;
        if (leftNull && rightNull)
            return SimulatedSqlException.IncompatibleDataTypesInOperator("NULL", "NULL", operatorName);
        var other = leftNull ? right : left;
        return other.Type.PairClass switch
        {
            TypePairClass.Bit or TypePairClass.Integer => null,
            TypePairClass.HierarchyId or TypePairClass.Spatial => SimulatedSqlException.InvalidOperatorForDataType(operatorName, OperandName(other)),
            TypePairClass.Vector => SimulatedSqlException.OperandDataTypeInvalid(OperandName(other), operatorName),
            _ => SimulatedSqlException.IncompatibleDataTypesInOperator(leftNull ? "NULL" : OperandName(left), rightNull ? "NULL" : OperandName(right), operatorName),
        };
    }

    /// <summary>
    /// A comparison types an integer literal by its value, which is the type
    /// its refusal names: 0 through 255 is <c>tinyint</c>, the rest of the
    /// 16-bit range (a negated literal included) <c>smallint</c>, and anything
    /// wider <c>int</c> — in a WHERE, a CASE or an IF alike, though arithmetic
    /// keeps <c>int</c> (probed 2026-09-25 against SQL Server 2025:
    /// <c>date = 0</c> names tinyint, <c>date = -1</c> smallint). All three
    /// share a pair class, so only the name changes.
    /// </summary>
    private static TypePairOperand NarrowedIntegerLiteral(TypePairOperand operand)
    {
        var source = operand.Source;
        while (source is Parser.Expressions.Parenthesized parenthesized)
            source = parenthesized.Wrapped;
        var negated = false;
        if (source is Parser.Expressions.Negate negate)
        {
            negated = true;
            source = negate.Operand;
        }
        if (source is not Parser.Expressions.Value { IsLiteral: true, IsUntypedNull: false } literal || literal.Constant.Type != Int32)
            return operand;
        var value = negated ? -(long)literal.Constant.AsInt32 : literal.Constant.AsInt32;
        SqlType narrowed = value is >= 0 and <= byte.MaxValue ? TinyInt : value is >= short.MinValue and <= short.MaxValue ? SmallInt : Int32;
        return new TypePairOperand(narrowed, operand.Source, operand.SourceTable, operand.SourceColumn);
    }

    private static bool IsMaxLengthVariantPair(SqlType maxSide, SqlType variantSide) =>
        variantSide is SqlVariantSqlType && IsMaxLength(maxSide);

    /// <summary>Whether <paramref name="type"/> is <c>varchar(max)</c>, <c>nvarchar(max)</c> or <c>varbinary(max)</c>.</summary>
    internal static bool IsMaxLength(SqlType type) =>
        type is VarcharSqlType { length: MaxLengthSentinel } or NVarcharSqlType { length: MaxLengthSentinel } or VarbinarySqlType { length: MaxLengthSentinel };

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
    /// <c>numeric</c> keeping that spelling. Real's Msg 8116 names an
    /// argument's type the same way.
    /// </summary>
    public static string OperandName(SqlType type, Parser.Expression? source) =>
        type is SystemNameSqlType ? "nvarchar"
        : type is DecimalSqlType && source is { ResultReportsNumeric: true } ? "numeric"
        : SimulatedSqlException.FamilyRootName(type);

    private static string OperandName(TypePairOperand operand) =>
        operand.ReportsNumeric && operand.Type is DecimalSqlType ? "numeric" : OperandName(operand.Type, operand.Source);

    /// <summary>
    /// What the grids can't say about a CLR user-defined type, which shares
    /// <c>hierarchyid</c>'s row and column (probed 2026-09-28 against SQL
    /// Server 2025): two different CLR types clash naming both
    /// schema-qualified, one against <c>hierarchyid</c> clashes by bare
    /// names — an assignment naming its source first, the other operations
    /// their right operand — and a type not marked <c>IsByteOrdered</c> can't
    /// be compared with itself (Msg 403).
    /// </summary>
    private static SimulatedSqlException? ClrUdtPairError(TypePairOperation operation, TypePairOperand left, TypePairOperand right, string operatorName)
    {
        var leftType = left.Type;
        var rightType = right.Type;
        if (leftType == rightType)
        {
            return operation == TypePairOperation.Compare && leftType is ClrUdtSqlType { Udt.IsByteOrdered: false } unordered
                ? SimulatedSqlException.InvalidOperatorForDataType(operatorName, unordered.Udt.Name)
                : null;
        }

        if (leftType.PairClass != TypePairClass.HierarchyId || rightType.PairClass != TypePairClass.HierarchyId)
            return null;

        static string Name(SqlType type, bool qualify) =>
            type is ClrUdtSqlType udt && qualify ? $"{udt.Udt.Schema.Name}.{udt.Udt.Name}" : type.ToString()!;
        var qualify = leftType is ClrUdtSqlType && rightType is ClrUdtSqlType;
        return operation == TypePairOperation.Assign
            ? SimulatedSqlException.OperandTypeClash(Name(leftType, qualify), Name(rightType, qualify))
            : SimulatedSqlException.OperandTypeClash(Name(rightType, qualify), Name(leftType, qualify));
    }
}
