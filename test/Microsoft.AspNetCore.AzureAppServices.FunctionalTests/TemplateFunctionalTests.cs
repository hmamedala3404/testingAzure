// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.AzureAppServices.FunctionalTests
{
    [Collection("Azure")]
    public class TemplateFunctionalTests
    {
        private const string RuntimeInformationMiddlewareType = "Microsoft.AspNetCore.AzureAppServices.FunctionalTests.RuntimeInformationMiddleware";

        private const string RuntimeInformationMiddlewareFile = "Templates\\RuntimeInformationMiddleware.cs";

        readonly AzureFixture _fixture;

        private readonly ITestOutputHelper _outputHelper;

        public TemplateFunctionalTests(AzureFixture fixture, ITestOutputHelper outputHelper)
        {
            _fixture = fixture;
            _outputHelper = outputHelper;
        }

        [Theory]
        [InlineData("web", "Hello World!")]
        [InlineData("razor", "Learn how to build ASP.NET apps that can run anywhere.")]
        [InlineData("mvc", "Learn how to build ASP.NET apps that can run anywhere.")]
        public async Task DotnetNewWebRunsInWebApp(string template, string expected)
        {
            var testId = nameof(DotnetNewWebRunsInWebApp) + template;

            using (var logger = GetLogger(testId))
            {
                var site = await _fixture.Deploy("Templates\\BasicAppServices.json", baseName: testId);
                var testDirectory = GetTestDirectory(testId);
                var dotnet = DotNet(logger, testDirectory, "2.0");

                await dotnet.ExecuteAndAssertAsync("new " + template);

                InjectMiddlware(testDirectory, RuntimeInformationMiddlewareType, RuntimeInformationMiddlewareFile);

                await site.BuildPublishProfileAsync(testDirectory.FullName);

                await dotnet.ExecuteAndAssertAsync("publish /p:PublishProfile=Profile");

                using (var httpClient = site.CreateClient())
                {
                    var getResult = await httpClient.GetAsync("/");
                    getResult.EnsureSuccessStatusCode();
                    Assert.Contains(expected, await getResult.Content.ReadAsStringAsync());

                    getResult = await httpClient.GetAsync("/runtimeInfo");
                    getResult.EnsureSuccessStatusCode();

                    var runtimeInfo = JsonConvert.DeserializeObject<RuntimeInfo>(await getResult.Content.ReadAsStringAsync());
                    ValidateRuntimeInfo(runtimeInfo, dotnet.Command);
                }
            }
        }

        [Theory]
        [InlineData("web", "Hello World!")]
        [InlineData("razor", "Learn how to build ASP.NET apps that can run anywhere.")]
        [InlineData("mvc", "Learn how to build ASP.NET apps that can run anywhere.")]
        public async Task DotnetNewWebRunsWebAppOnLatestRuntime(string template, string expected)
        {
            var testId = nameof(DotnetNewWebRunsWebAppOnLatestRuntime) + template;

            using (var logger = GetLogger(testId))
            {
                var site = await _fixture.Deploy("Templates\\AppServicesWithSiteExtensions.json",
                    baseName: testId,
                    additionalArguments: new Dictionary<string, string>
                    {
                        { "extensionFeed", AzureFixture.GetRequiredEnvironmentVariable("SiteExtensionFeed") },
                        { "extensionName", "AspNetCoreTestBundle" },
                        { "extensionVersion", GetAssemblyInformationalVersion() },
                    });

                var testDirectory = GetTestDirectory(testId);
                var dotnet = DotNet(logger, testDirectory, "latest");

                await dotnet.ExecuteAndAssertAsync("new " + template);

                InjectMiddlware(testDirectory, RuntimeInformationMiddlewareType, RuntimeInformationMiddlewareFile);

                FixAspNetCoreVersion(testDirectory, dotnet.Command);

                await dotnet.ExecuteAndAssertAsync("restore");

                await site.BuildPublishProfileAsync(testDirectory.FullName);

                await dotnet.ExecuteAndAssertAsync("publish /p:PublishProfile=Profile");

                using (var httpClient = site.CreateClient())
                {
                    var getResult = await httpClient.GetAsync("/");
                    getResult.EnsureSuccessStatusCode();
                    Assert.Contains(expected, await getResult.Content.ReadAsStringAsync());

                    getResult = await httpClient.GetAsync("/runtimeInfo");
                    getResult.EnsureSuccessStatusCode();

                    var runtimeInfo = JsonConvert.DeserializeObject<RuntimeInfo>(await getResult.Content.ReadAsStringAsync());
                    ValidateRuntimeInfo(runtimeInfo, dotnet.Command);
                }
            }
        }

        private void ValidateRuntimeInfo(RuntimeInfo runtimeInfo, string dotnetPath)
        {
            var storeModules = PathUtilities.GetStoreModules(dotnetPath);

            var runtimeModules = PathUtilities.GetSharedRuntimeAssemblies(dotnetPath);

            foreach (var runtimeInfoModule in runtimeInfo.Modules)
            {
                if (storeModules.Any(f => runtimeInfoModule.ModuleName.StartsWith(f, StringComparison.InvariantCultureIgnoreCase)))
                {
                    Assert.Contains("store\\x86\\netcoreapp2.0\\", runtimeInfoModule.FileName);
                }

                // Native modules would prefer to be loaded from windows folder, skip them
                if (runtimeModules.Any(f => runtimeInfoModule.ModuleName.StartsWith(f, StringComparison.InvariantCultureIgnoreCase)) &&
                    runtimeInfoModule.FileName.IndexOf("windows\\system32", StringComparison.InvariantCultureIgnoreCase) == -1)
                {
                    Assert.Contains("shared\\Microsoft.NETCore.App\\", runtimeInfoModule.FileName);
                }
            }
        }

        private static void InjectMiddlware(DirectoryInfo projectRoot, string typeName, string fileName)
        {
            // Copy implementation file to project directory
            var implementationFile = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            var destinationImplementationFile = Path.Combine(projectRoot.FullName, Path.GetFileName(fileName));
            File.Copy(implementationFile, destinationImplementationFile, true);

            // Register middleware in Startup.cs/Configure
            var startupFile = Path.Combine(projectRoot.FullName, "Startup.cs");
            var startupText = File.ReadAllText(startupFile);
            startupText = Regex.Replace(startupText, "public void Configure\\([^{]+{", match => match.Value + $" app.UseMiddleware<{typeName}>();");
            File.WriteAllText(startupFile, startupText);
        }

        private static void FixAspNetCoreVersion(DirectoryInfo testDirectory, string dotnetPath)
        {
            // TODO: Temporary workaround for broken templates in latest CLI

            // Detect what version of aspnet core was shipped with this CLI installation
            var aspnetCoreVersion = PathUtilities.GetBundledAspNetCoreVersion(dotnetPath);

            var csproj = testDirectory.GetFiles("*.csproj").Single().FullName;
            var projectContents = XDocument.Load(csproj);
            var packageReferences = projectContents
                .Descendants("PackageReference")
                .Concat(projectContents.Descendants("DotNetCliToolReference"));

            foreach (var packageReference in packageReferences)
            {
                var packageName = (string)packageReference.Attribute("Include");

                if (packageName == "Microsoft.AspNetCore.All" ||
                    packageName == "Microsoft.VisualStudio.Web.CodeGeneration.Tools")
                {
                    packageReference.Attribute("Version").Value = aspnetCoreVersion;
                }
            }

            projectContents.Save(csproj);
        }

        private string GetAssemblyInformationalVersion()
        {
            var assemblyInformationalVersionAttribute = typeof(TemplateFunctionalTests).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (assemblyInformationalVersionAttribute == null)
            {
                throw new InvalidOperationException("Tests assembly lacks AssemblyInformationalVersionAttribute");
            }
            return assemblyInformationalVersionAttribute.InformationalVersion;
        }

        private TestLogger GetLogger([CallerMemberName] string callerName = null)
        {
            _fixture.TestLog.StartTestLog(_outputHelper, nameof(TemplateFunctionalTests), out var factory, callerName);
            return new TestLogger(factory, factory.CreateLogger(callerName));
        }

        private TestCommand DotNet(TestLogger logger, DirectoryInfo workingDirectory, string sufix)
        {
            return new TestCommand(GetDotNetPath(sufix))
            {
                Logger = logger,
                WorkingDirectory = workingDirectory.FullName
            };
        }

        private static string GetDotNetPath(string sufix)
        {
            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null)
            {
                var dotnetSubdir = new DirectoryInfo(Path.Combine(current.FullName, ".test-dotnet", sufix));
                if (dotnetSubdir.Exists)
                {
                    var dotnetName = Path.Combine(dotnetSubdir.FullName, "dotnet.exe");
                    if (!File.Exists(dotnetName))
                    {
                        throw new InvalidOperationException("dotnet directory was found but dotnet.exe is not in it");
                    }
                    return dotnetName;
                }
                current = current.Parent;
            }

            throw new InvalidOperationException("dotnet executable was not found");
        }


        private DirectoryInfo GetTestDirectory([CallerMemberName] string callerName = null)
        {
            if (Directory.Exists(callerName))
            {
                Directory.Delete(callerName, recursive:true);
            }
            return Directory.CreateDirectory(callerName);
        }
    }
}