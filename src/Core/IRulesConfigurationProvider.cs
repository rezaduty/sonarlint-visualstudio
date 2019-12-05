﻿/*
 * SonarLint for Visual Studio
 * Copyright (C) 2016-2018 SonarSource SA
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

using System.Threading;
using System.Threading.Tasks;
using SonarQube.Client.Models;

namespace SonarLint.VisualStudio.Core.Binding
{
    public interface IRulesConfigurationProvider
    {
        Task<IRulesConfigurationFile> GetRulesConfigurationAsync(SonarQubeQualityProfile qualityProfile, string organizationKey, Language language, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Abstraction of the various types of configuration file used to store rule configuration information
    /// </summary>
    /// <remarks>e.g. for C# and VB.NET the rules configuration will be in a ruleset file.
    /// For C++ it will be in a json file in a Sonar-specific format</remarks>
    public interface IRulesConfigurationFile
    {
        // WIP - this is currently just a marker interface
    }
}
