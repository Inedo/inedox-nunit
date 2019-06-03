using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using Inedo.Agents;
using Inedo.Diagnostics;
using Inedo.Documentation;
using Inedo.Extensibility;
using Inedo.Extensibility.Operations;

namespace Inedo.Extensions.NUnit.Operations
{
    [Tag("unit-tests")]
    [ScriptAlias("Execute-TestProject")]
    [ScriptAlias("Execute-NUnit", Obsolete = true)]
    [DisplayName("Execute NUnit Tests")]
    [Description("Runs NUnit unit tests on a specified project, assembly, or NUnit file.")]
    [ScriptNamespace("NUnit")]
    public sealed class NUnitOperation : ExecuteOperation
    {
        [Required]
        [ScriptAlias("TestFile")]
        [DisplayName("Test file")]
        [Description("The file NUnit will test against (could be dll, proj, or config file based on test runner).")]
        public string TestFile { get; set; }
        [ScriptAlias("Arguments")]
        [DisplayName("Additional arguments")]
        [Description("Raw command line arguments passed to the NUnit test runner.")]
        public string AdditionalArguments { get; set; }
        [ScriptAlias("OutputFile")]
        [ScriptAlias("OutputDirectory", Obsolete = true)]
        [DisplayName("Output file")]
        [PlaceholderText("(randomly generated name)")]
        public string CustomXmlOutputPath { get; set; }

        [Category("Advanced")]
        [ScriptAlias("Group")]
        [DisplayName("Group name")]
        [Description("When multiple sets of tests are performed, unique group names will categorize them in the UI.")]
        [PlaceholderText("NUnit")]
        public string GroupName { get; set; }
        [Category("Advanced")]
        [ScriptAlias("IsNUnit3")]
        [DisplayName("Is NUnit v3")]
        [Description("When set to true, a different syntax will be used for command-line arguments.")]
        [DefaultValue(true)]
        public bool IsNUnit3 { get; set; } = true;
        [Category("Advanced")]
        [ScriptAlias("NUnitExePath")]
        [DisplayName("nunit path")]
        [Description("The path to the nunit test runner executable.")]
        [DefaultValue("$NUnitConsolePath")]
        public string ExePath { get; set; }

        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {
            var fileOps = await context.Agent.GetServiceAsync<IFileOperationsExecuter>();
            var testFilePath = context.ResolvePath(this.TestFile);
            this.LogDebug("Test file: " + testFilePath);

            if (!await fileOps.FileExistsAsync(testFilePath))
            {
                this.LogError($"Test file {testFilePath} does not exist.");
                return;
            }

            var exePath = context.ResolvePath(this.ExePath);
            if (await fileOps.FileExistsAsync(exePath))
            {
                this.LogDebug("Exe path: " + exePath);
            }
            else
            {
                exePath = this.ExePath;
                // different message formatting to assit with debugging
                this.LogDebug("Using executable: " + exePath);
            }

            string outputFilePath;
            if (string.IsNullOrEmpty(this.CustomXmlOutputPath))
                outputFilePath = fileOps.CombinePath(context.WorkingDirectory, Guid.NewGuid().ToString("N") + ".xml");
            else
                outputFilePath = context.ResolvePath(this.CustomXmlOutputPath);

            this.LogDebug("Output file: " + outputFilePath);

            var args = this.IsNUnit3 
                ? $"\"{testFilePath}\" --result:\"{outputFilePath}\";format=nunit2"
                : $"\"{testFilePath}\" /xml:\"{outputFilePath}\"";
            
            if (!string.IsNullOrEmpty(this.AdditionalArguments))
            {
                this.LogDebug("Additional arguments: " + this.AdditionalArguments);
                args += " " + this.AdditionalArguments;
            }

            try
            {
                await this.ExecuteCommandLineAsync(
                    context,
                    new RemoteProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = args,
                        WorkingDirectory = context.WorkingDirectory
                    }
                );

                XDocument xdoc;
                using (var stream = await fileOps.OpenFileAsync(outputFilePath, FileMode.Open, FileAccess.Read))
                {
                    xdoc = XDocument.Load(stream);
                }

#if DEBUG
                this.LogDebug(xdoc.ToString());
#endif

                var testResultsElement = xdoc.Element("test-results");

                var startTime = this.TryParseStartTime((string)testResultsElement.Attribute("date"), (string)testResultsElement.Attribute("time")) ?? DateTime.UtcNow;
                var failures = 0;

                var testRecorder = await context.TryGetServiceAsync<IUnitTestRecorder>();
                foreach (var testCaseElement in xdoc.Descendants("test-case"))
                {
                    var testName = (string)testCaseElement.Attribute("name");

                    // skip tests that weren't actually run
                    if (string.Equals((string)testCaseElement.Attribute("executed"), "False", StringComparison.OrdinalIgnoreCase))
                    {
                        this.LogInformation($"NUnit test: {testName} (skipped)");
                        continue;
                    }

                    var result = AH.Switch<string, UnitTestStatus>((string)testCaseElement.Attribute("success"), StringComparer.OrdinalIgnoreCase)
                        .Case("True", UnitTestStatus.Passed)
                        .Case("Inconclusive", UnitTestStatus.Inconclusive)
                        .Default(UnitTestStatus.Failed)
                        .End();
                    if (result == UnitTestStatus.Failed)
                        failures++;

                    var testDuration = this.TryParseTestTime((string)testCaseElement.Attribute("time"));

                    this.LogInformation($"NUnit test: {testName}, Result: {result}, Test length: {testDuration}");

                    if (testRecorder != null)
                    {
                        await testRecorder.RecordUnitTestAsync(
                            groupName: AH.NullIf(this.GroupName, string.Empty) ?? "NUnit",
                            testName: testName,
                            testStatus: result,
                            testResult: testCaseElement.ToString(),
                            startTime: startTime,
                            duration: testDuration
                        );
                    }

                    startTime += testDuration;
                }

                if (failures > 0)
                    this.LogError($"{failures} test failures were reported.");
            }
            finally
            {
                if (string.IsNullOrEmpty(this.CustomXmlOutputPath))
                {
                    this.LogDebug($"Deleting temp output file ({outputFilePath})...");
                    try
                    {
                        await fileOps.DeleteFileAsync(outputFilePath);
                    }
                    catch
                    {
                        this.LogWarning($"Could not delete {outputFilePath}.");
                    }
                }
            }
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            var longActionDescription = new RichDescription();
            if (!string.IsNullOrWhiteSpace(config[nameof(this.AdditionalArguments)]))
            {
                longActionDescription.AppendContent(
                    "with additional arguments: ",
                    new Hilite(config[nameof(this.AdditionalArguments)])
                );
            }

            return new ExtendedRichDescription(
                new RichDescription(
                    "Run NUnit on ",
                    new DirectoryHilite(config[nameof(this.TestFile)])
                ),
                longActionDescription
            );
        }

        private DateTime? TryParseStartTime(string date, string time)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(date))
                {
                    if (DateTime.TryParse(time, out DateTime result))
                        return result.ToUniversalTime();
                }

                if (!string.IsNullOrWhiteSpace(date) && !string.IsNullOrWhiteSpace(time))
                {
                    var dateParts = date.Split('-');
                    var timeParts = time.Split(':');

                    return new DateTime(
                        year: int.Parse(dateParts[0]),
                        month: int.Parse(dateParts[1]),
                        day: int.Parse(dateParts[2]),
                        hour: int.Parse(timeParts[0]),
                        minute: int.Parse(timeParts[1]),
                        second: int.Parse(timeParts[2])
                    ).ToUniversalTime();
                }
            }
            catch
            {
            }

            this.LogWarning("Unable to parse start time; using current time instead.");
            return null;
        }
        private TimeSpan TryParseTestTime(string time)
        {
            if (string.IsNullOrWhiteSpace(time))
                return TimeSpan.Zero;

            var mungedTime = time.Replace(',', '.');
            bool parsed = double.TryParse(
                mungedTime,
                NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out double doubleTime
            );

            if (!parsed)
                this.LogWarning($"Could not parse {time} as a time in seconds.");

            return TimeSpan.FromSeconds(doubleTime);
        }
    }
}
