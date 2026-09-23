namespace SqlServerSimulator.Storage;

/// <summary>
/// The well-formedness failures real's XML parser reports while converting a
/// value to <c>xml</c>, each its own message in the 9400 family; see
/// <see cref="SimulatedSqlException.XmlParsingFailed"/> for the numbers and
/// wordings. Declared in message-number order.
/// </summary>
internal enum XmlParseError : byte
{
    UnexpectedEndOfInput,
    UnrecognizedEncoding,
    UnableToSwitchEncoding,
    WhitespaceExpected,
    SemicolonExpected,
    GreaterThanExpected,
    StringLiteralExpected,
    EqualExpected,
    LessThanInAttributeValue,
    HexadecimalDigitExpected,
    DecimalDigitExpected,
    IllegalXmlCharacter,
    IllegalNameCharacter,
    IncorrectDocumentSyntax,
    IncorrectCDataSyntax,
    IncorrectCommentSyntax,
    EndTagMismatch,
    DuplicateAttribute,
    XmlDeclarationNotAtBeginning,
    ReservedXmlName,
    IncorrectXmlDeclarationSyntax,
    UndeclaredEntity,
    IncorrectProcessingInstructionSyntax,
    CDataEndInContent,
    IllegalQualifiedNameCharacter,
    MultipleColons,
    RedeclaredPrefix,
    UndeclaredPrefix,
    EmptyNamespaceUri,
    XmlPrefixRebound,
    XmlnsPrefixDeclared,
}
