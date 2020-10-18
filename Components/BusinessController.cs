// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information

namespace Dnn.Modules.ModuleCreator.Components
{
    using System;

    using DotNetNuke.Entities.Modules;

    /// <summary>
    /// The module business controller is used to implement Dnn module interfaces.
    /// </summary>
    public class BusinessController : IUpgradeable
    {
        /// <summary>
        /// Runs when the module is upgraded.
        /// </summary>
        /// <param name="version">The version of the new package.</param>
        /// <returns>"Success" or "Failed".</returns>
        public string UpgradeModule(string version)
        {
            try
            {
                switch (version)
                {
                    case "08.00.00":

                        break;
                }

                return "Success";
            }
            catch (Exception)
            {
                return "Failed";
            }
        }
    }
}
