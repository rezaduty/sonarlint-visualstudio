﻿/*
 * SonarLint for Visual Studio
 * Copyright (C) 2016-2020 SonarSource SA
 * mailto:info AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using EnvDTE;
using FluentAssertions;
using Microsoft.VisualStudio.CodeAnalysis.RuleSets;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SonarLint.VisualStudio.Core.Binding;
using SonarLint.VisualStudio.Integration.Binding;
using SonarLint.VisualStudio.Integration.NewConnectedMode;
using SonarQube.Client.Models;
using Language = SonarLint.VisualStudio.Core.Language;

namespace SonarLint.VisualStudio.Integration.UnitTests.Binding
{
    [TestClass]
    public class SolutionBindingOperationTests
    {
        private DTEMock dte;
        private ConfigurableServiceProvider serviceProvider;
        private ConfigurableVsProjectSystemHelper projectSystemHelper;
        private ConfigurableVsOutputWindowPane outputPane;
        private ProjectMock solutionItemsProject;
        private SolutionMock solutionMock;
        private ConfigurableSourceControlledFileSystem sccFileSystem;

        // Note: currently the project binding saves files using the IRuleSetSerializer.
        // However, solution binding saves files using IBindingConfigFileWithRuleSet.Save(...)
        // -> a test might need to mock both.
        // If/when the project binding switches to IBindingConfigFileWithRuleSet.Save(...)
        // then the tests can be simplified.
        private ConfigurableRuleSetSerializer ruleFS;

        private ConfigurableSolutionRuleSetsInformationProvider ruleSetInfo;

        private const string SolutionRoot = @"c:\solution";

        [TestInitialize]
        public void TestInitialize()
        {
            this.dte = new DTEMock();
            this.serviceProvider = new ConfigurableServiceProvider();
            this.solutionMock = new SolutionMock(dte, Path.Combine(SolutionRoot, "xxx.sln"));
            this.outputPane = new ConfigurableVsOutputWindowPane();
            this.serviceProvider.RegisterService(typeof(SVsGeneralOutputWindowPane), this.outputPane);
            this.projectSystemHelper = new ConfigurableVsProjectSystemHelper(this.serviceProvider);
            this.solutionItemsProject = this.solutionMock.AddOrGetProject("Solution items");
            this.projectSystemHelper.SolutionItemsProject = this.solutionItemsProject;
            this.projectSystemHelper.CurrentActiveSolution = this.solutionMock;
            this.sccFileSystem = new ConfigurableSourceControlledFileSystem();
            this.ruleFS = new ConfigurableRuleSetSerializer(this.sccFileSystem);
            this.ruleSetInfo = new ConfigurableSolutionRuleSetsInformationProvider
            {
                SolutionRootFolder = SolutionRoot
            };

            this.serviceProvider.RegisterService(typeof(IProjectSystemHelper), this.projectSystemHelper);
            this.serviceProvider.RegisterService(typeof(ISourceControlledFileSystem), this.sccFileSystem);
            this.serviceProvider.RegisterService(typeof(IRuleSetSerializer), this.ruleFS);
            this.serviceProvider.RegisterService(typeof(ISolutionRuleSetsInformationProvider), this.ruleSetInfo);
        }

        #region Tests

        [TestMethod]
        public void SolutionBindingOperation_ArgChecks()
        {
            var connectionInformation = new ConnectionInformation(new Uri("http://valid"));
            var logger = new TestLogger();
            Exceptions.Expect<ArgumentNullException>(() => new SolutionBindingOperation(null, connectionInformation, "key", "name", SonarLintMode.LegacyConnected, logger));
            Exceptions.Expect<ArgumentNullException>(() => new SolutionBindingOperation(this.serviceProvider, null, "key", "name", SonarLintMode.LegacyConnected, logger));
            Exceptions.Expect<ArgumentNullException>(() => new SolutionBindingOperation(this.serviceProvider, connectionInformation, null, "name", SonarLintMode.LegacyConnected, logger));
            Exceptions.Expect<ArgumentNullException>(() => new SolutionBindingOperation(this.serviceProvider, connectionInformation, string.Empty, "name", SonarLintMode.LegacyConnected, logger));

            Exceptions.Expect<ArgumentOutOfRangeException>(() => new SolutionBindingOperation(this.serviceProvider, connectionInformation, "123", "name", SonarLintMode.Standalone, logger));
            Exceptions.Expect<ArgumentNullException>(() => new SolutionBindingOperation(this.serviceProvider, connectionInformation, "123", "name", SonarLintMode.LegacyConnected, null));

            var testSubject = new SolutionBindingOperation(this.serviceProvider, connectionInformation, "key", "name", SonarLintMode.LegacyConnected, logger);
            testSubject.Should().NotBeNull("Avoid 'testSubject' not used analysis warning");
        }

        [TestMethod]
        public void SolutionBindingOperation_RegisterKnownRuleSets_ArgChecks()
        {
            // Arrange
            SolutionBindingOperation testSubject = this.CreateTestSubject("key");

            // Act + Assert
            Exceptions.Expect<ArgumentNullException>(() => testSubject.RegisterKnownConfigFiles(null));
        }

        [TestMethod]
        public void SolutionBindingOperation_RegisterKnownRuleSets()
        {
            // Arrange
            SolutionBindingOperation testSubject = this.CreateTestSubject("key");
            var languageToFileMap = new Dictionary<Language, IBindingConfigFile>();
            languageToFileMap[Language.CSharp] = CreateMockRuleSetConfigFile("cs").Object;
            languageToFileMap[Language.VBNET] = CreateMockRuleSetConfigFile("vb").Object;

            // Sanity
            testSubject.RuleSetsInformationMap.Should().BeEmpty("Not expecting any registered rulesets");

            // Act
            testSubject.RegisterKnownConfigFiles(languageToFileMap);

            // Assert
            CollectionAssert.AreEquivalent(languageToFileMap.Keys.ToArray(), testSubject.RuleSetsInformationMap.Keys.ToArray());
            testSubject.RuleSetsInformationMap[Language.CSharp].BindingConfigFile.Should().Be(languageToFileMap[Language.CSharp]);
            testSubject.RuleSetsInformationMap[Language.VBNET].BindingConfigFile.Should().Be(languageToFileMap[Language.VBNET]);
        }

        [TestMethod]
        public void SolutionBindingOperation_GetRuleSetInformation()
        {
            // Arrange
            SolutionBindingOperation testSubject = this.CreateTestSubject("key");

            // Test case 1: unknown ruleset map
            // Act + Assert
            using (new AssertIgnoreScope())
            {
                testSubject.GetConfigFileInformation(Language.CSharp).Should().BeNull();
            }

            // Test case 2: known ruleset map
            // Arrange
            var ruleSetMap = new Dictionary<Language, IBindingConfigFile>();
            ruleSetMap[Language.CSharp] = CreateMockRuleSetConfigFile("cs").Object;
            ruleSetMap[Language.VBNET] = CreateMockRuleSetConfigFile("vb").Object;

            testSubject.RegisterKnownConfigFiles(ruleSetMap);
            testSubject.Initialize(new ProjectMock[0], GetQualityProfiles());
            testSubject.Prepare(CancellationToken.None);

            // Act
            string filePath = testSubject.GetConfigFileInformation(Language.CSharp).NewFilePath;

            // Assert
            string.IsNullOrWhiteSpace(filePath).Should().BeFalse();
            filePath.Should().Be(testSubject.RuleSetsInformationMap[Language.CSharp].NewFilePath, "NewRuleSetFilePath is expected to be updated during Prepare and returned now");
        }

        [TestMethod]
        public void SolutionBindingOperation_Initialization_ArgChecks()
        {
            // Arrange
            SolutionBindingOperation testSubject = this.CreateTestSubject("key");

            // Act + Assert
            Exceptions.Expect<ArgumentNullException>(() => testSubject.Initialize(null, GetQualityProfiles()));
            Exceptions.Expect<ArgumentNullException>(() => testSubject.Initialize(new Project[0], null));
        }

        [TestMethod]
        public void SolutionBindingOperation_Initialization()
        {
            // Arrange
            var cs1Project = this.solutionMock.AddOrGetProject("CS1.csproj");
            cs1Project.SetCSProjectKind();
            var cs2Project = this.solutionMock.AddOrGetProject("CS2.csproj");
            cs2Project.SetCSProjectKind();
            var vbProject = this.solutionMock.AddOrGetProject("VB.vbproj");
            vbProject.SetVBProjectKind();
            var otherProjectType = this.solutionMock.AddOrGetProject("xxx.proj");
            otherProjectType.ProjectKind = "{" + Guid.NewGuid().ToString() + "}";

            var logger = new TestLogger();

            SolutionBindingOperation testSubject = this.CreateTestSubject("key", logger: logger);
            var projects = new[] { cs1Project, vbProject, cs2Project, otherProjectType };

            // Sanity
            testSubject.Binders.Should().BeEmpty("Not expecting any project binders");

            // Act
            testSubject.Initialize(projects, GetQualityProfiles());

            // Assert
            testSubject.SolutionFullPath.Should().Be(Path.Combine(SolutionRoot, "xxx.sln"));
            testSubject.Binders.Should().HaveCount(3, "Should be one per managed project");

            testSubject.Binders.Select(x => ((ProjectBindingOperation)x).ProjectFullPath)
                .Should().BeEquivalentTo("CS1.csproj", "CS2.csproj", "VB.vbproj");

            logger.AssertPartialOutputStringExists("xxx.proj"); // expecting a message about the project that won't be bound, but not the others
            logger.AssertPartialOutputStringDoesNotExist("CS1.csproj");
        }

        [TestMethod]
        public void SolutionBindingOperation_Prepare()
        {
            // Arrange
            var csProject = this.solutionMock.AddOrGetProject("CS.csproj");
            csProject.SetCSProjectKind();
            var vbProject = this.solutionMock.AddOrGetProject("VB.vbproj");
            vbProject.SetVBProjectKind();
            var projects = new[] { csProject, vbProject };

            SolutionBindingOperation testSubject = this.CreateTestSubject("key");

            var csConfigFile = CreateMockRuleSetConfigFile("cs");
            var vbConfigFile = CreateMockRuleSetConfigFile("vb");
            var ruleSetMap = new Dictionary<Language, IBindingConfigFile>();
            ruleSetMap[Language.CSharp] = csConfigFile.Object;
            ruleSetMap[Language.VBNET] = vbConfigFile.Object;

            testSubject.RegisterKnownConfigFiles(ruleSetMap);
            testSubject.Initialize(projects, GetQualityProfiles());
            testSubject.Binders.Clear(); // Ignore the real binders, not part of this test scope
            var binder = new ConfigurableBindingOperation();
            testSubject.Binders.Add(binder);
            bool prepareCalledForBinder = false;
            binder.PrepareAction = (ct) => prepareCalledForBinder = true;
            string sonarQubeRulesDirectory = Path.Combine(SolutionRoot, ConfigurableSolutionRuleSetsInformationProvider.DummyLegacyModeFolderName);

            var csharpRulesetPath = Path.Combine(sonarQubeRulesDirectory, "keycsharp.ruleset");
            var vbRulesetPath = Path.Combine(sonarQubeRulesDirectory, "keyvb.ruleset");

            // Sanity
            this.sccFileSystem.directories.Should().NotContain(sonarQubeRulesDirectory);
            testSubject.RuleSetsInformationMap[Language.CSharp].NewFilePath.Should().Be(csharpRulesetPath);
            testSubject.RuleSetsInformationMap[Language.VBNET].NewFilePath.Should().Be(vbRulesetPath);

            // Act
            testSubject.Prepare(CancellationToken.None);

            // Assert
            this.sccFileSystem.directories.Should().NotContain(sonarQubeRulesDirectory);
            prepareCalledForBinder.Should().BeTrue("Expected to propagate the prepare call to binders");
            CheckSaveWasNotCalled(csConfigFile);
            CheckSaveWasNotCalled(vbConfigFile);

            // Act (write pending)
            this.sccFileSystem.WritePendingNoErrorsExpected();

            // Assert
            CheckRuleSetFileWasSaved(csConfigFile, csharpRulesetPath);
            CheckRuleSetFileWasSaved(vbConfigFile, vbRulesetPath);
            this.sccFileSystem.directories.Should().Contain(sonarQubeRulesDirectory);
        }

        [TestMethod]
        public void SolutionBindingOperation_Prepare_Cancellation_DuringBindersPrepare()
        {
            // Arrange
            var csProject = this.solutionMock.AddOrGetProject("CS.csproj");
            csProject.SetCSProjectKind();
            var vbProject = this.solutionMock.AddOrGetProject("VB.vbproj");
            vbProject.SetVBProjectKind();
            var projects = new[] { csProject, vbProject };

            SolutionBindingOperation testSubject = this.CreateTestSubject("key");

            var csConfigFile = CreateMockRuleSetConfigFile("cs");
            var vbConfigFile = CreateMockRuleSetConfigFile("vb");
            var languageToFileMap = new Dictionary<Language, IBindingConfigFile>();
            languageToFileMap[Language.CSharp] = csConfigFile.Object;
            languageToFileMap[Language.VBNET] = vbConfigFile.Object;

            testSubject.RegisterKnownConfigFiles(languageToFileMap);
            testSubject.Initialize(projects, GetQualityProfiles());
            testSubject.Binders.Clear(); // Ignore the real binders, not part of this test scope
            bool prepareCalledForBinder = false;
            using (CancellationTokenSource src = new CancellationTokenSource())
            {
                testSubject.Binders.Add(new ConfigurableBindingOperation { PrepareAction = (t) => src.Cancel() });
                testSubject.Binders.Add(new ConfigurableBindingOperation { PrepareAction = (t) => prepareCalledForBinder = true });

                // Act
                testSubject.Prepare(src.Token);
            }

            // Assert
            string expectedSolutionFolder = Path.Combine(SolutionRoot, ConfigurableSolutionRuleSetsInformationProvider.DummyLegacyModeFolderName);
            testSubject.RuleSetsInformationMap[Language.CSharp].NewFilePath.Should().Be(Path.Combine(expectedSolutionFolder, "keycsharp.ruleset"));
            testSubject.RuleSetsInformationMap[Language.VBNET].NewFilePath.Should().Be(Path.Combine(expectedSolutionFolder, "keyvb.ruleset"));
            prepareCalledForBinder.Should().BeFalse("Expected to be canceled as soon as possible i.e. after the first binder");

            CheckSaveWasNotCalled(csConfigFile);
            CheckSaveWasNotCalled(vbConfigFile);
        }

        [TestMethod]
        public void SolutionBindingOperation_Prepare_Cancellation_BeforeBindersPrepare()
        {
            // Arrange
            var csProject = this.solutionMock.AddOrGetProject("CS.csproj");
            csProject.SetCSProjectKind();
            var vbProject = this.solutionMock.AddOrGetProject("VB.vbproj");
            vbProject.SetVBProjectKind();
            var projects = new[] { csProject, vbProject };

            SolutionBindingOperation testSubject = this.CreateTestSubject("key");

            var csConfigFile = CreateMockRuleSetConfigFile("cs");
            var vbConfigFile = CreateMockRuleSetConfigFile("vb");
            var ruleSetMap = new Dictionary<Language, IBindingConfigFile>();
            ruleSetMap[Language.CSharp] = csConfigFile.Object;
            ruleSetMap[Language.VBNET] = vbConfigFile.Object;

            testSubject.RegisterKnownConfigFiles(ruleSetMap);
            testSubject.Initialize(projects, GetQualityProfiles());
            testSubject.Binders.Clear(); // Ignore the real binders, not part of this test scope
            bool prepareCalledForBinder = false;
            using (CancellationTokenSource src = new CancellationTokenSource())
            {
                testSubject.Binders.Add(new ConfigurableBindingOperation { PrepareAction = (t) => prepareCalledForBinder = true });
                src.Cancel();

                // Act
                testSubject.Prepare(src.Token);
            }

            // Assert
            testSubject.RuleSetsInformationMap[Language.CSharp].NewFilePath.Should().NotBeNull("Expected to be set before Prepare is called");
            testSubject.RuleSetsInformationMap[Language.VBNET].NewFilePath.Should().NotBeNull("Expected to be set before Prepare is called");
            prepareCalledForBinder.Should().BeFalse("Expected to be canceled as soon as possible i.e. before the first binder");
            CheckSaveWasNotCalled(csConfigFile);
            CheckSaveWasNotCalled(vbConfigFile);
        }

        [TestMethod]
        public void SolutionBindingOperation_CommitSolutionBinding_LegacyConnectedMode()
        {
            // Act & Assert
            ExecuteCommitSolutionBindingTest(SonarLintMode.LegacyConnected);

            var expectedRuleset = Path.Combine(SolutionRoot, ConfigurableSolutionRuleSetsInformationProvider.DummyLegacyModeFolderName, "keyCSharp.ruleset");
            this.solutionItemsProject.Files.ContainsKey(expectedRuleset).Should().BeTrue("Ruleset was expected to be added to solution items when in legacy mode");
            this.sccFileSystem.files.Should().ContainKey(expectedRuleset); // check the file was saved
        }

        [TestMethod]
        public void SolutionBindingOperation_CommitSolutionBinding_ConnectedMode()
        {
            // Act & Assert
            ExecuteCommitSolutionBindingTest(SonarLintMode.Connected);

            this.solutionItemsProject.Files.Count.Should().Be(0, "Not expecting any items to be added to the solution in new connected mode");
            var expectedRuleset = Path.Combine(SolutionRoot, ConfigurableSolutionRuleSetsInformationProvider.DummyConnectedModeFolderName, "keyCSharp.ruleset");
            this.sccFileSystem.files.Should().ContainKey(expectedRuleset); // check the file was saved
        }

        private void ExecuteCommitSolutionBindingTest(SonarLintMode bindingMode)
        {
            // Arrange
            var configProvider = new ConfigurableConfigurationProvider();
            this.serviceProvider.RegisterService(typeof(IConfigurationProvider), configProvider);
            var csProject = this.solutionMock.AddOrGetProject("CS.csproj");
            csProject.SetCSProjectKind();
            var projects = new[] { csProject };

            var connectionInformation = new ConnectionInformation(new Uri("http://xyz"));
            SolutionBindingOperation testSubject = this.CreateTestSubject("key", connectionInformation, bindingMode);

            var configFileMock = CreateMockRuleSetConfigFile("cs");
            var languageToFileMap = new Dictionary<Language, IBindingConfigFile>()
            {
                { Language.CSharp, configFileMock.Object }
            };
            
            testSubject.RegisterKnownConfigFiles(languageToFileMap);
            var profiles = GetQualityProfiles();

            DateTime expectedTimeStamp = DateTime.Now;
            profiles[Language.CSharp] = new SonarQubeQualityProfile("expected profile Key", "", "", false, expectedTimeStamp);
            testSubject.Initialize(projects, profiles);
            testSubject.Binders.Clear(); // Ignore the real binders, not part of this test scope
            bool commitCalledForBinder = false;
            testSubject.Binders.Add(new ConfigurableBindingOperation { CommitAction = () => commitCalledForBinder = true });
            testSubject.Prepare(CancellationToken.None);

            // Sanity
            configProvider.SavedConfiguration.Should().BeNull();

            // Act
            var commitResult = testSubject.CommitSolutionBinding();

            // Assert
            commitResult.Should().BeTrue();
            commitCalledForBinder.Should().BeTrue();

            configProvider.SavedConfiguration.Should().NotBeNull();
            configProvider.SavedConfiguration.Mode.Should().Be(bindingMode);

            var savedProject = configProvider.SavedConfiguration.Project;
            savedProject.ServerUri.Should().Be(connectionInformation.ServerUri);
            savedProject.Profiles.Should().HaveCount(1);
            savedProject.Profiles[Language.CSharp].ProfileKey.Should().Be("expected profile Key");
            savedProject.Profiles[Language.CSharp].ProfileTimestamp.Should().Be(expectedTimeStamp);
        }

        [TestMethod]
        public void SolutionBindingOperation_ConfigFileInformation_Ctor_ArgChecks()
        {
            Action act = () => new ConfigFileInformation(null);
            act.Should().ThrowExactly<ArgumentNullException>().And.ParamName.Should().Be("bindingConfigFile");
        }

        #endregion Tests

        #region Helpers

        private SolutionBindingOperation CreateTestSubject(string projectKey,
            ConnectionInformation connection = null,
            SonarLintMode bindingMode = SonarLintMode.LegacyConnected,
            ILogger logger = null)
        {
            return new SolutionBindingOperation(this.serviceProvider,
                connection ?? new ConnectionInformation(new Uri("http://host")),
                projectKey,
                projectKey,
                bindingMode,
                logger ?? new TestLogger());
        }

        private static Dictionary<Language, SonarQubeQualityProfile> GetQualityProfiles()
        {
            return new Dictionary<Language, SonarQubeQualityProfile>();
        }

        private Mock<IBindingConfigFileWithRuleset> CreateMockRuleSetConfigFile(string displayName)
        {
            var rulesetConfig = new Mock<IBindingConfigFileWithRuleset>();
            rulesetConfig.Setup(x => x.RuleSet)
                .Returns(new RuleSet(displayName));

            // Simulate an update to the scc file system on Save (prevents an assertion
            // in the product code).
            rulesetConfig.Setup(x => x.Save(It.IsAny<string>()))
                .Callback<string>(s => this.sccFileSystem.UpdateTimestamp(s));

            return rulesetConfig;
        }

        private static void CheckRuleSetFileWasSaved(Mock<IBindingConfigFileWithRuleset> mock, string expectedFileName)
            => mock.Verify(x => x.Save(expectedFileName), Times.Once);

        private static void CheckSaveWasNotCalled(Mock<IBindingConfigFileWithRuleset> mock)
            => mock.Verify(x => x.Save(It.IsAny<string>()), Times.Never);



        #endregion Helpers
    }
}
