using System.Globalization;
using System.Text;
using HerdMe.Windows.Models;

internal static partial class ContractChecks
{
    internal static void VerifyDumpParsing()
    {
        static string Encode(string serialized) => Convert.ToBase64String(Encoding.UTF8.GetBytes(serialized));
        static string SerializeString(string value) => $"s:{Encoding.UTF8.GetByteCount(value)}:\"{value}\";";
        static void Reject(string serialized, string reason)
        {
            var payload = Encode(serialized);
            var captured = CapturedDump.Decode(payload);
            Check(captured.Summary.StartsWith("Unable to parse VarDumper payload:", StringComparison.Ordinal)
                && captured.Payload == payload, reason);
        }

        const string source = "D:\\مشروع\\تجربة.php";
        const string message = "مرحبا 🌍 — café";
        var serialized = "a:2:{" + SerializeString("context") + "a:1:{" + SerializeString("file")
            + SerializeString(source) + "}" + SerializeString("message") + SerializeString(message) + "}";
        var capture = CapturedDump.Decode(Encode(serialized));
        Check(capture.Source == source && capture.Summary.Contains(message, StringComparison.Ordinal),
            "VarDumper preserves Arabic, accented text, and emoji using serialized UTF-8 byte lengths");

        var objectCapture = CapturedDump.Decode(Encode("O:8:\"stdClass\":2:{"
            + SerializeString("\0*\0source") + SerializeString(source)
            + SerializeString("value") + "R:1;}"));
        Check(objectCapture.Source == source && objectCapture.Summary.Contains("reference(1)", StringComparison.Ordinal),
            "VarDumper retains object property visibility and reference markers");

        var nestedContext = "a:1:{" + SerializeString("file") + SerializeString(source) + "}";
        for (var depth = 0; depth < 32; depth++) nestedContext = "a:1:{i:0;" + nestedContext + "}";
        Check(CapturedDump.Decode(Encode(nestedContext)).Source == source,
            "Ordinary nested dump context still discovers source files beyond the preview depth");

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var numeric = CapturedDump.Decode(Encode("a:7:{i:0;i:-9223372036854775808;i:1;d:-1.25E+2;"
                + "i:2;b:0;i:3;b:1;i:4;d:INF;i:5;d:-INF;i:6;d:NAN;}"));
            Check(numeric.Summary.Contains("-9223372036854775808", StringComparison.Ordinal)
                && numeric.Summary.Contains("-125", StringComparison.Ordinal)
                && numeric.Summary.Contains("false", StringComparison.Ordinal)
                && numeric.Summary.Contains("true", StringComparison.Ordinal)
                && numeric.Summary.Contains("Infinity", StringComparison.Ordinal)
                && numeric.Summary.Contains("NaN", StringComparison.Ordinal),
                "PHP numbers and special floating values decode independently of the Windows locale");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }

        string[] malformedValues =
        [
            "b:2;", "b:false;", "b:;", "i: 1;", "i:9223372036854775808;", "d:1,25;",
            "d: 1.25;", "d:1e9999;", "d:Infinity;", "d:NaN;", "d:1.2.3;", "R:0;", "r:-1;",
            "s:-1:\"\";", "s:+1:\"x\";", "s:2147483647:\"x\";", "s:3:\"🌍\";",
            "a:1:{N;N;}", "a:1:{a:0:{}N;}", "a:1:{i:0;N;", "O:2147483647:\"x\":0:{}",
            "N;N;", "N; ", "", "a:2147483647:{}"
        ];
        foreach (var value in malformedValues)
        {
            Reject(value, $"Malformed PHP input uses the fallback summary: {value}");
        }

        var nested = string.Concat(Enumerable.Repeat("a:1:{i:0;", 10_000)) + "N;" + new string('}', 10_000);
        Reject(nested, "Deeply nested VarDumper input is bounded before it can exhaust the process stack");
        Reject("a:60000:{" + string.Concat(Enumerable.Repeat("i:0;N;", 60_000)) + "}",
            "Wide VarDumper collections are bounded before constructing a large value tree");
        var branch = "i:0;a:10000:{" + string.Concat(Enumerable.Repeat("i:0;N;", 10_000)) + "}";
        Reject("a:10:{" + string.Concat(Enumerable.Repeat(branch, 10)) + "}",
            "The VarDumper node budget covers the entire tree across nested collections");
        Reject("i:" + new string('0', 1_000_000) + ";",
            "Oversized numeric tokens use the fallback without allocating an unbounded token string");
        Reject(SerializeString(new string('x', 4 * 1_024 * 1_024 + 1)),
            "Oversized serialized strings do not reach the dump preview");

        var oversizedPayload = new string('A', 16 * 1_024 * 1_024 + 1);
        Check(CapturedDump.Decode(oversizedPayload).Summary.StartsWith("Unable to parse VarDumper payload:", StringComparison.Ordinal),
            "Oversized base64 input is rejected before allocating its decoded buffer");
        Check(CapturedDump.Decode(Encode("a:1:{i:0;" + SerializeString("valid after malformed input") + "}"))
            .Summary.Contains("valid after malformed input", StringComparison.Ordinal),
            "Valid captures continue to decode after rejected adversarial payloads");
    }
}
