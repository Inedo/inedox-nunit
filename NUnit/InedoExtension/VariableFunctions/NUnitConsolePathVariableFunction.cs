using Inedo.Documentation;
using Inedo.Extensibility;
using Inedo.Extensibility.VariableFunctions;

namespace Inedo.Extensions.NUnit.VariableFunctions
{
    [Tag("unit-tests")]
    [ScriptAlias("NUnitConsolePath")]
    [ExtensionConfigurationVariable]
    public sealed class NUnitConsolePathVariableFunction : ScalarVariableFunction
    {
        protected override object EvaluateScalar(IVariableFunctionContext context) => "nunit-console.exe";
    }
}
