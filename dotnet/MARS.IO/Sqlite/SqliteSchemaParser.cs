// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Pulls column names out of a CREATE TABLE statement.

using System;
using System.Collections.Generic;
using System.Text;

namespace MARS.IO.Sqlite;

/// <summary>
/// Recovers the column order of a table from its stored CREATE TABLE text.
/// <para>
/// SQLite keeps no separate column catalog, so the DDL is the only record of order, and the
/// record format is positional. BiblioSpec's schema is heavily commented, so line comments
/// and string literals both have to be stripped before splitting on commas.
/// </para>
/// </summary>
public static class SqliteSchemaParser
{
    private static readonly string[] ConstraintKeywords =
    {
        "PRIMARY", "FOREIGN", "UNIQUE", "CHECK", "CONSTRAINT",
    };

    public static IReadOnlyList<string> ParseColumns(string createSql)
    {
        if (string.IsNullOrWhiteSpace(createSql)) return Array.Empty<string>();

        string sql = StripComments(createSql);

        int open = sql.IndexOf('(');
        if (open < 0) return Array.Empty<string>();

        int close = FindMatchingParen(sql, open);
        if (close < 0) return Array.Empty<string>();

        string body = sql[(open + 1)..close];
        var columns = new List<string>();

        foreach (string part in SplitTopLevel(body))
        {
            string definition = part.Trim();
            if (definition.Length == 0) continue;

            string first = FirstToken(definition);
            if (first.Length == 0) continue;

            var isConstraint = false;
            foreach (string keyword in ConstraintKeywords)
            {
                if (string.Equals(first, keyword, StringComparison.OrdinalIgnoreCase))
                {
                    isConstraint = true;
                    break;
                }
            }

            if (isConstraint) continue;
            columns.Add(Unquote(first));
        }

        return columns;
    }

    private static string StripComments(string sql)
    {
        var text = new StringBuilder(sql.Length);
        var inString = false;
        char stringQuote = '\0';

        for (var i = 0; i < sql.Length; i++)
        {
            char c = sql[i];

            if (inString)
            {
                text.Append(c);
                if (c == stringQuote) inString = false;
                continue;
            }

            if (c is '\'' or '"' or '`' or '[')
            {
                inString = true;
                stringQuote = c == '[' ? ']' : c;
                text.Append(c);
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                text.Append('\n');
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i++;
                text.Append(' ');
                continue;
            }

            text.Append(c);
        }

        return text.ToString();
    }

    private static int FindMatchingParen(string sql, int open)
    {
        var depth = 0;
        var inString = false;
        char stringQuote = '\0';

        for (int i = open; i < sql.Length; i++)
        {
            char c = sql[i];

            if (inString)
            {
                if (c == stringQuote) inString = false;
                continue;
            }

            switch (c)
            {
                case '\'':
                case '"':
                case '`':
                    inString = true;
                    stringQuote = c;
                    break;
                case '[':
                    inString = true;
                    stringQuote = ']';
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }

    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var start = 0;
        var inString = false;
        char stringQuote = '\0';

        for (var i = 0; i < body.Length; i++)
        {
            char c = body[i];

            if (inString)
            {
                if (c == stringQuote) inString = false;
                continue;
            }

            switch (c)
            {
                case '\'':
                case '"':
                case '`':
                    inString = true;
                    stringQuote = c;
                    break;
                case '[':
                    inString = true;
                    stringQuote = ']';
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return body[start..i];
                    start = i + 1;
                    break;
            }
        }

        if (start < body.Length) yield return body[start..];
    }

    private static string FirstToken(string definition)
    {
        var i = 0;
        while (i < definition.Length && char.IsWhiteSpace(definition[i])) i++;
        if (i >= definition.Length) return string.Empty;

        if (definition[i] is '"' or '`' or '[')
        {
            char closing = definition[i] == '[' ? ']' : definition[i];
            int end = definition.IndexOf(closing, i + 1);
            return end < 0 ? definition[i..] : definition[i..(end + 1)];
        }

        int start = i;
        while (i < definition.Length && !char.IsWhiteSpace(definition[i]) &&
               definition[i] != '(' && definition[i] != ',')
        {
            i++;
        }

        return definition[start..i];
    }

    private static string Unquote(string token)
    {
        if (token.Length < 2) return token;
        char first = token[0];
        char last = token[^1];
        if ((first == '"' && last == '"') || (first == '`' && last == '`') || (first == '[' && last == ']'))
            return token[1..^1];
        return token;
    }
}
