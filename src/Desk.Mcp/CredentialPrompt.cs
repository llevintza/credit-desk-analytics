namespace Desk.Mcp;

/// <summary>Where <c>desk-mcp login</c> reads the email and the password: the terminal, never arguments or the environment.</summary>
internal interface ICredentialPrompt
{
    /// <summary>False when there is no terminal to prompt on (input redirected).</summary>
    bool Interactive { get; }

    string ReadLine(string prompt);

    /// <summary>Reads without echoing.</summary>
    string ReadSecret(string prompt);
}

internal sealed class ConsolePrompt : ICredentialPrompt
{
    public bool Interactive => !Console.IsInputRedirected;

    public string ReadLine(string prompt)
    {
        Console.Error.Write(prompt);
        return Console.ReadLine() ?? "";
    }

    public string ReadSecret(string prompt)
    {
        Console.Error.Write(prompt);
        var chars = new List<char>();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
            else if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.Error.WriteLine();
        return new string([.. chars]);
    }
}
