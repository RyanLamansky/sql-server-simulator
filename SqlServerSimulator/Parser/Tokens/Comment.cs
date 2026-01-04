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
    /// Parses a single-line comment (`-- ...`) where the initial `--` has already been consumed.
    /// </summary>
    /// <param name="start">The position of the `--`.</param>
    /// <param name="index">Initially after the opening `--`, updated to just after the end of the line.</param>
    /// <param name="command">The raw command to parse.</param>
    /// <returns>A <see cref="Comment"/>.</returns>
    public static Comment ParseSingleLine(int start, ref int index, string command)
    {
        while (++index < command.Length)
        {
            switch (command[index])
            {
                case '\r':
                case '\n':
                    return new Comment(command, start, index-- - start);
            }
        }

        index--;

        return new Comment(command, start, 2);
    }

    /// <summary>
    /// Parses a block-style comment (`/* ... */`) where the initial `/*` has already been consumed.
    /// </summary>
    /// <param name="start">The position of the opening `/`.</param>
    /// <param name="index">Initially after the opening `*`, updated to just after the closing `/`.</param>
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
                            return new Comment(command, start, ++index - start + 1);

                        depth--;
                    }
                    continue;
            }
        }

        throw SimulatedSqlException.MissingEndCommentMark();
    }
}
