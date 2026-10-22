namespace OneImage.Tests.Filter;

internal static class TestNotes
{
    private static readonly object s_gate = new();

    /// <summary>Reports a measurement to the test log and, when ONEIMAGE_TEST_NOTES names a file, appends it there.</summary>
    public static void Add(string text)
    {
        TestContext.Current.SendDiagnosticMessage(text);
        string? path = Environment.GetEnvironmentVariable("ONEIMAGE_TEST_NOTES");
        if (!string.IsNullOrEmpty(path))
        {
            lock (s_gate)
            {
                File.AppendAllText(path, text + Environment.NewLine);
            }
        }
    }
}
