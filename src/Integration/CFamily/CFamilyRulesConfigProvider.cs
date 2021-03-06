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

using System.ComponentModel.Composition;
using System.Diagnostics;
using SonarLint.VisualStudio.Core;
using SonarLint.VisualStudio.Core.CFamily;
using SonarLint.VisualStudio.Core.SystemAbstractions;
using SonarLint.VisualStudio.Integration.NewConnectedMode;

// Note: currently this class needs to be in the the Integration assembly because it
//   references IHost and other internal interfaces.

namespace SonarLint.VisualStudio.Integration.CFamily
{
    [Export(typeof(ICFamilyRulesConfigProvider))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal class CFamilyRuleConfigProvider : ICFamilyRulesConfigProvider
    {
        private readonly IActiveSolutionBoundTracker activeSolutionBoundTracker;
        private readonly IUserSettingsProvider userSettingsProvider;
        private readonly EffectiveRulesConfigCalculator effectiveConfigCalculator;
        private readonly ILogger logger;

        // Settable in constructor for testing
        private readonly ICFamilyRulesConfigProvider sonarWayProvider;

        // Local services
        private readonly ISolutionRuleSetsInformationProvider solutionInfoProvider;

        private readonly UserSettingsSerializer serializer;

        [ImportingConstructor]
        public CFamilyRuleConfigProvider(IHost host, IActiveSolutionBoundTracker activeSolutionBoundTracker, IUserSettingsProvider userSettingsProvider, ILogger logger)
            : this(host, activeSolutionBoundTracker, userSettingsProvider, logger,
                 new CFamilySonarWayRulesConfigProvider(CFamilyShared.CFamilyFilesDirectory),
                 new FileWrapper())
        {
        }

        public CFamilyRuleConfigProvider(IHost host, IActiveSolutionBoundTracker activeSolutionBoundTracker, IUserSettingsProvider userSettingsProvider,
            ILogger logger, ICFamilyRulesConfigProvider sonarWayProvider, IFile fileWrapper)
        {
            this.activeSolutionBoundTracker = activeSolutionBoundTracker;
            this.userSettingsProvider = userSettingsProvider;
            this.logger = logger;

            solutionInfoProvider = host.GetService<ISolutionRuleSetsInformationProvider>();
            solutionInfoProvider.AssertLocalServiceIsNotNull();

            this.sonarWayProvider = sonarWayProvider;
            this.serializer = new UserSettingsSerializer(fileWrapper, logger);

            this.effectiveConfigCalculator = new EffectiveRulesConfigCalculator(logger);
        }

        #region IRulesConfigurationProvider implementation

        public ICFamilyRulesConfig GetRulesConfiguration(string languageKey)
        {
            UserSettings settings = null;

            // If in connected mode, look for the C++/C family settings in the .sonarlint/sonarqube folder.
            var binding = this.activeSolutionBoundTracker.CurrentConfiguration;
            if (binding != null && binding.Mode != SonarLintMode.Standalone)
            {
                settings = FindConnectedModeSettings(languageKey, binding);
                if (settings == null)
                {
                    logger.WriteLine(Resources.Strings.CFamily_UnableToLoadConnectedModeSettings);
                }
                else
                {
                    logger.WriteLine(Resources.Strings.CFamily_UsingConnectedModeSettings);
                }
            }

            // If we are not in connected mode or couldn't find the connected mode settings then fall back on the standalone settings.
            settings = settings ?? userSettingsProvider.UserSettings;
            var sonarWayConfig = sonarWayProvider.GetRulesConfiguration(languageKey);
            return CreateConfiguration(languageKey, sonarWayConfig, settings);
        }

        #endregion IRulesConfigurationProvider implementation

        private UserSettings FindConnectedModeSettings(string languageKey, BindingConfiguration binding)
        {
            var language = Language.GetLanguageFromLanguageKey(languageKey);
            Debug.Assert(language != null, $"Unknown language key: {languageKey}");

            if (language != null)
            {
                var filePath = solutionInfoProvider.CalculateSolutionSonarQubeRuleSetFilePath(binding.Project.ProjectKey, language, binding.Mode);
                var settings = serializer.SafeLoad(filePath);
                return settings;
            }
            return null;
        }

        protected virtual /* for testing */ ICFamilyRulesConfig CreateConfiguration(string languageKey, ICFamilyRulesConfig sonarWayConfig, UserSettings settings)
            => effectiveConfigCalculator.GetEffectiveRulesConfig(languageKey, sonarWayConfig, settings);
    }
}
