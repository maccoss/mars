// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;

namespace MARS.Cli;

/// <summary>
/// A supplied option is not one the command understands. Raised before the command starts
/// work, and fatal - see <see cref="CommandLineArgs.RejectUnknown"/> for why this is not a
/// warning.
/// </summary>
public sealed class UnknownOptionException : Exception
{
    public UnknownOptionException(string message)
        : base(message)
    {
    }
}
