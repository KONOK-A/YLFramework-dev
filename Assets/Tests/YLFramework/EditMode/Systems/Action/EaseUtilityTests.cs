using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Actions;

namespace YLFramework.Tests.Actions
{
    public class EaseUtilityTests
    {
        private const float Tolerance = 0.00001f;

        private static readonly string[] _expectedNames =
        {
            "Linear", "InOutBack", "InBack", "OutBack", "InOutBounce", "OutBounce", "InBounce",
            "InOutCircle", "OutCircle", "InCircle", "InOutCubic", "OutCubic", "InCubic",
            "InOutElastic", "OutElastic", "InElastic", "InOutExpo", "OutExpo", "InExpo",
            "InOutQuad", "OutQuad", "InQuad", "InoutQuart", "OutQuart", "InQuart",
            "InOutQuint", "OutQuint", "InQuint", "InOutSine", "OutSine", "InSine", "Count"
        };

        [Test]
        public void EaseTypePreservesEveryNameAndNumericValue()
        {
            CollectionAssert.AreEqual(_expectedNames, Enum.GetNames(typeof(EaseType)));
            for (var index = 0; index < _expectedNames.Length; index++)
            {
                Assert.That((int)Enum.Parse(typeof(EaseType), _expectedNames[index]), Is.EqualTo(index));
            }
        }

        [Test]
        public void PublicApiPreservesAllMethodsParametersAndDefaults()
        {
            Assert.That(new EaseUtility(), Is.Not.Null);
            Assert.That(typeof(EaseUtility).Namespace, Is.EqualTo("YLFramework.Actions"));
            Assert.That(typeof(EaseUtility).IsAbstract, Is.False);
            Assert.That(typeof(EaseUtility).IsSealed, Is.False);
            var methods = typeof(EaseUtility).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Assert.That(methods.Length, Is.EqualTo(31));

            for (var index = 0; index < (int)EaseType.Count; index++)
            {
                var name = _expectedNames[index] == "InoutQuart" ? "InOutQuart" : _expectedNames[index];
                var method = typeof(EaseUtility).GetMethod(name);
                Assert.That(method, Is.Not.Null, name);
                Assert.That(method.ReturnType, Is.EqualTo(typeof(float)), name);
                Assert.That(method.IsGenericMethod, Is.False, name);
                var parameters = method.GetParameters();
                var isBack = name.EndsWith("Back", StringComparison.Ordinal);
                var isBounce = name.EndsWith("Bounce", StringComparison.Ordinal);
                var isElastic = name.EndsWith("Elastic", StringComparison.Ordinal);
                Assert.That(parameters.Length, Is.EqualTo(isElastic ? 5 : isBack || isBounce ? 4 : 3), name);
                var requiredNames = new[] { "start", "end", "pos" };
                for (var parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                {
                    Assert.That(parameters[parameterIndex].ParameterType, Is.EqualTo(typeof(float)), name);
                    if (parameterIndex < 3)
                    {
                        Assert.That(parameters[parameterIndex].Name, Is.EqualTo(requiredNames[parameterIndex]), name);
                        Assert.That(parameters[parameterIndex].IsOptional, Is.False, name);
                    }
                    else
                    {
                        Assert.That(parameters[parameterIndex].IsOptional, Is.True, name);
                    }
                }

                if (isBack || isBounce)
                {
                    Assert.That(parameters[3].Name, Is.EqualTo("bounciness"), name);
                    Assert.That(parameters[3].DefaultValue, Is.EqualTo(isBack ? 1.5f : 7.5625f), name);
                }
                else if (isElastic)
                {
                    Assert.That(parameters[3].Name, Is.EqualTo("elasticity"), name);
                    Assert.That(parameters[3].DefaultValue, Is.EqualTo(0.3f), name);
                    Assert.That(parameters[4].Name, Is.EqualTo("duration"), name);
                    Assert.That(parameters[4].DefaultValue, Is.EqualTo(5.0f), name);
                }
            }
        }

        [Test]
        public void SharedRuntimeRetainsExistingDependenciesWithoutOptionalActionAssemblies()
        {
            var assembly = typeof(EaseUtility).Assembly;
            Assert.That(assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var name = reference.Name;
                Assert.That(name.StartsWith("Sirenix", StringComparison.Ordinal), Is.False, "Odin dependency must not return: " + name);
                var allowed = name == "Newtonsoft.Json" || name == "UnityEditor.CoreModule" || name == "UnityEditor.UI" || name == "UnityEngine.UI" ||
                              name == "mscorlib" || name == "netstandard" || name == "System" ||
                              name.StartsWith("System.", StringComparison.Ordinal) ||
                               name == "UnityEngine" || name.StartsWith("UnityEngine.", StringComparison.Ordinal);
                if (name == "BouncyCastle.Cryptography")
                {
                    var runtime = UnityEditor.Compilation.CompilationPipeline.GetAssemblies()
                        .Single(item => item.name == assembly.GetName().Name);
                    var crypto = runtime.compiledAssemblyReferences.Single(path => System.IO.Path.GetFileName(path) == "BouncyCastle.Cryptography.dll");
                    allowed = TestDependencyGuard.IsApprovedBouncyCastleReference(crypto);
                }
                Assert.That(allowed, Is.True, "Unexpected runtime dependency: " + name);
            }
        }

        [TestCase(nameof(EaseUtility.Linear), 0.25f)]
        [TestCase(nameof(EaseUtility.InOutBack), -0.03125f)]
        [TestCase(nameof(EaseUtility.InBack), -0.0546875f)]
        [TestCase(nameof(EaseUtility.OutBack), 0.7890625f)]
        [TestCase(nameof(EaseUtility.InOutBounce), -0.3828125f)]
        [TestCase(nameof(EaseUtility.OutBounce), 0.47265625f)]
        [TestCase(nameof(EaseUtility.InBounce), -0.22265625f)]
        [TestCase(nameof(EaseUtility.InOutCircle), 0.06698730f)]
        [TestCase(nameof(EaseUtility.OutCircle), 0.66143783f)]
        [TestCase(nameof(EaseUtility.InCircle), 0.03175416f)]
        [TestCase(nameof(EaseUtility.InOutCubic), 0.0625f)]
        [TestCase(nameof(EaseUtility.OutCubic), 0.578125f)]
        [TestCase(nameof(EaseUtility.InCubic), 0.015625f)]
        [TestCase(nameof(EaseUtility.InOutElastic), -0.0078125f)]
        [TestCase(nameof(EaseUtility.OutElastic), 0.91161165f)]
        [TestCase(nameof(EaseUtility.InElastic), -0.00552427f)]
        [TestCase(nameof(EaseUtility.InOutExpo), 0.015625f)]
        [TestCase(nameof(EaseUtility.OutExpo), 0.82322330f)]
        [TestCase(nameof(EaseUtility.InExpo), 0.00552427f)]
        [TestCase(nameof(EaseUtility.InOutQuad), 0.125f)]
        [TestCase(nameof(EaseUtility.OutQuad), 0.4375f)]
        [TestCase(nameof(EaseUtility.InQuad), 0.0625f)]
        [TestCase(nameof(EaseUtility.InOutQuart), 0.03125f)]
        [TestCase(nameof(EaseUtility.OutQuart), 0.68359375f)]
        [TestCase(nameof(EaseUtility.InQuart), 0.00390625f)]
        [TestCase(nameof(EaseUtility.InOutQuint), 0.015625f)]
        [TestCase(nameof(EaseUtility.OutQuint), 0.76269531f)]
        [TestCase(nameof(EaseUtility.InQuint), 0.0009765625f)]
        [TestCase(nameof(EaseUtility.InOutSine), 0.14644661f)]
        [TestCase(nameof(EaseUtility.OutSine), 0.38268343f)]
        [TestCase(nameof(EaseUtility.InSine), 0.07612047f)]
        public void AllCurvesMatchFrozenQuarterPointValues(string name, float expected)
        {
            var method = typeof(EaseUtility).GetMethod(name);
            Assert.That(method, Is.Not.Null, name);
            var parameters = method.GetParameters();
            var arguments = new object[parameters.Length];
            arguments[0] = 0f;
            arguments[1] = 1f;
            arguments[2] = 0.25f;
            for (var index = 3; index < arguments.Length; index++)
            {
                arguments[index] = parameters[index].DefaultValue;
            }

            Assert.That((float)method.Invoke(null, arguments), Is.EqualTo(expected).Within(Tolerance), name);
        }

        [TestCase(-1f, 2f)]
        [TestCase(0f, 2f)]
        [TestCase(0.5f, 4f)]
        [TestCase(1f, 6f)]
        [TestCase(2f, 6f)]
        public void LinearPreservesClamping(float position, float expected)
        {
            Assert.That(EaseUtility.Linear(2f, 6f, position), Is.EqualTo(expected));
        }

        [TestCase(0f, 4f, 0.5f, 1f)]
        [TestCase(4f, 0f, 0.5f, 3f)]
        [TestCase(-2f, 2f, 0.5f, -1f)]
        [TestCase(0f, 1f, 2f, 4f)]
        public void QuadraticPreservesDirectionAndDoesNotClamp(float start, float end, float position, float expected)
        {
            Assert.That(EaseUtility.InQuad(start, end, position), Is.EqualTo(expected));
        }

        [Test]
        public void ExplicitBackAndElasticParametersReachTheirAlgorithms()
        {
            Assert.That(EaseUtility.InBack(0f, 1f, 0.5f, 0f), Is.EqualTo(0.125f));
            Assert.That(EaseUtility.OutBack(0f, 1f, 0.5f, 0f), Is.EqualTo(0.875f));
            Assert.That(EaseUtility.InOutBack(0f, 1f, 0.25f, 0f), Is.EqualTo(0.0625f));
            Assert.That(EaseUtility.OutBounce(0f, 1f, 0.25f, 4f), Is.EqualTo(0.25f));
            Assert.That(EaseUtility.InElastic(0f, 1f, 0.5f, 0.5f, 2f), Is.EqualTo(0.03125f).Within(Tolerance));
            Assert.That(EaseUtility.OutElastic(0f, 1f, 0.5f, 0.5f, 2f), Is.EqualTo(0.96875f).Within(Tolerance));
            Assert.That(EaseUtility.InOutElastic(0f, 1f, 0.25f, 0.5f, 2f), Is.EqualTo(0.015625f).Within(Tolerance));
            Assert.That(EaseUtility.InOutElastic(0f, 1f, 0.75f, 0.5f, 2f), Is.EqualTo(0.984375f).Within(Tolerance));
        }

        [TestCase(0.1f, 0.075625f)]
        [TestCase(0.5f, 0.765625f)]
        [TestCase(0.8f, 0.94f)]
        [TestCase(0.95f, 0.98453125f)]
        public void OutBouncePreservesAllFourSegments(float position, float expected)
        {
            Assert.That(EaseUtility.OutBounce(0f, 1f, position), Is.EqualTo(expected).Within(Tolerance));
        }

        [Test]
        public void LegacyBounceQuirksRemainCharacterizedNotApprovedAsCorrectCurves()
        {
            // AK-924: retain the local baseline until a separate behavior change is approved.
            Assert.That(EaseUtility.InBounce(0f, 1f, 1f), Is.EqualTo(0f));
            Assert.That(EaseUtility.InOutBounce(0f, 1f, 0.25f, 1f),
                Is.EqualTo(EaseUtility.InOutBounce(0f, 1f, 0.25f, 20f)));
            Assert.That(EaseUtility.InOutBounce(0f, 1f, 0.75f, 1f),
                Is.EqualTo(EaseUtility.InOutBounce(0f, 1f, 0.75f, 20f)));
        }

        [Test]
        public void LegacyExpoEndpointsRemainCharacterized()
        {
            Assert.That(EaseUtility.InExpo(0f, 1f, 0f), Is.EqualTo(1f / 1024f));
            Assert.That(EaseUtility.OutExpo(0f, 1f, 1f), Is.EqualTo(1023f / 1024f));
            Assert.That(EaseUtility.InOutExpo(0f, 1f, 0f), Is.EqualTo(1f / 2048f));
            Assert.That(EaseUtility.InOutExpo(0f, 1f, 1f), Is.EqualTo(2047f / 2048f));
        }

        [Test]
        public void CircleAndElasticEdgeCasesAreNotSilentlyClamped()
        {
            Assert.That(float.IsNaN(EaseUtility.InCircle(0f, 1f, 2f)), Is.True);
            Assert.That(float.IsNaN(EaseUtility.OutCircle(0f, 1f, -1f)), Is.True);
            Assert.That(EaseUtility.OutElastic(1f, -1f, 0f), Is.EqualTo(1f));
            Assert.That(EaseUtility.InElastic(1f, -1f, 0.995f), Is.EqualTo(-1f));
            Assert.That(EaseUtility.OutElastic(2f, 2f, 0.4f), Is.EqualTo(2f));
            Assert.That(float.IsNaN(EaseUtility.InElastic(0f, 1f, 0.5f, 0f, 5f)), Is.True);
        }
    }
}
