namespace SqlServerSimulator.Parser.Tokens;

sealed class Comment : Token
{
    private Comment(string command, int index, int length) : base(command, index, length)
    {
        System.Diagnostics.Debug.Assert(length >= 2);
        System.Diagnostics.Debug.Assert(command[index] is '/' or '-' && command[index + 1] is '*' or '-');
        System.Diagnostics.Debug.Assert(command[index] is not '/' || length >= 4);
    }

    /// <summary>
    /// Parses a single-line comment (<c>-- ...</c>) where the initial <c>--</c> has already been consumed.
    /// </summary>
    /// <param name="start">The position of the <c>--</c>.</param>
    /// <param name="index">Initially after the opening <c>--</c>; updated to the next un-read character past the comment (the line break, or end of command).</param>
    /// <param name="command">The raw command to parse.</param>
    /// <returns>A <see cref="Comment"/>.</returns>
    public static Comment ParseSingleLine(int start, ref int index, string command)
    {
        while (++index < command.Length)
        {
            if (command[index] is '\r' or '\n')
                return new Comment(command, start, index - start);
        }

        return new Comment(command, start, 2);
    }

    /// <summary>
    /// Parses a block-style comment (<c>/* ... */</c>) where the initial <c>/*</c> has already been consumed.
    /// </summary>
    /// <param name="start">The position of the opening <c>/</c>.</param>
    /// <param name="index">Initially after the opening <c>*</c>; updated to the next un-read character past the closing <c>*/</c>.</param>
    /// <param name="command">The raw command to parse.</param>
    /// <returns>A <see cref="Comment"/>.</returns>
    /// <exception cref="SimulatedSqlException">Missing end comment mark '*/'.</exception>
    public static Comment ParseBlock(int start, ref int index, string command)
    {
        var depth = 0;
        while (++index < command.Length - 1)
        {
            switch (command[index])
            {
                case '/':
                    if (command[index + 1] == '*')
                        depth++;
                    continue;
                case '*':
                    if (command[index + 1] == '/')
                    {
                        if (depth == 0)
                        {
                            index += 2;
                            return new Comment(command, start, index - start);
                        }

                        depth--;
                    }
                    continue;
            }
        }

        throw SimulatedSqlException.MissingEndCommentMark();
    }
}
