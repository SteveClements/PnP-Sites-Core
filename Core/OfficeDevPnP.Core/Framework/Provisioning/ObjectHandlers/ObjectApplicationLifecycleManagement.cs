﻿using Microsoft.SharePoint.Client;
using OfficeDevPnP.Core.ALM;
using OfficeDevPnP.Core.Diagnostics;
using OfficeDevPnP.Core.Framework.Provisioning.Model;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OfficeDevPnP.Core.Framework.Provisioning.ObjectHandlers
{
#if !ONPREMISES
    internal class ObjectApplicationLifecycleManagement : ObjectHandlerBase
    {
        public override string Name
        {
            get { return "Application Lifecycle Management"; }
        }

        public override string InternalName => "ApplicationLifecycleManagement";

        public override ProvisioningTemplate ExtractObjects(Web web, ProvisioningTemplate template, ProvisioningTemplateCreationInformation creationInfo)
        {
            using (var scope = new PnPMonitoredScope(this.Name))
            {
                // The ALM API do not support the local Site Collection App Catalog
                // Thus, so far we skip the AppCatalog section
                // NOOP

                // Process the collection of Apps installed in the current Site Collection
                var appCatalogUri = web.GetAppCatalog();
                if (appCatalogUri != null)
                {
                    var manager = new AppManager(web.Context as ClientContext);

                    var siteApps = manager.GetAvailable()?.Where(a => a.InstalledVersion != null)?.ToList();
                    if (siteApps != null && siteApps.Count > 0)
                    {
                        foreach (var app in siteApps)
                        {
                            template.ApplicationLifecycleManagement.Apps.Add(new Model.App
                            {
                                AppId = app.Id.ToString(),
                                Action = AppAction.Install,
                            });
                        }
                    }
                }
            }
            return template;
        }

        public override TokenParser ProvisionObjects(Web web, ProvisioningTemplate template, TokenParser parser, ProvisioningTemplateApplyingInformation applyingInformation)
        {
            using (var scope = new PnPMonitoredScope(this.Name))
            {
                if (template.ApplicationLifecycleManagement != null)
                {
                    var manager = new AppManager(web.Context as ClientContext);
					                    
                    if (template.ApplicationLifecycleManagement.Apps != null &&
                        template.ApplicationLifecycleManagement.Apps.Count > 0)
                    {
                        //Get tenant app catalog
                        var appCatalogUri = web.GetAppCatalog();
                        if (appCatalogUri != null)
                        {
                            // Get the apps already installed in the site
                            var siteApps = manager.GetAvailable()?.Where(a => a.InstalledVersion != null)?.ToList();
							var allAppsTenant = manager.GetAvailable().ToList();

							List<AppMetadata> siteAppsSiteCollection = new List<AppMetadata>();
							List<AppMetadata> allAppsSiteCollection = new List<AppMetadata>();

							try
							{
								siteAppsSiteCollection = manager.GetAvailable(Enums.AppCatalogScope.Site)?.Where(a => a.InstalledVersion != null)?.ToList();
								allAppsSiteCollection = manager.GetAvailable(Enums.AppCatalogScope.Site).ToList();
							}
							catch
							{
								// No site collection app catatlog
							}

							Enums.AppCatalogScope GetAppScope(Guid appId)
							{
								if(allAppsTenant.Any(a => a.Id == appId))
								{
									return Enums.AppCatalogScope.Tenant;
								}
								else if (allAppsSiteCollection.Any(a => a.Id == appId))
								{
									return Enums.AppCatalogScope.Site;
								}
								// Should probably make this nullable, but doing this will just let it fall through the normal exceptions.
								return Enums.AppCatalogScope.Tenant;
							}

							foreach (var app in template.ApplicationLifecycleManagement.Apps)
                            {
								Guid appId = Guid.Empty;
								try
								{
									appId = Guid.Parse(parser.ParseString(app.AppId));
								}
								catch
								{
									//{apppackageid:FastStart Web Parts}
									var appName = app.AppId.Substring(app.AppId.LastIndexOf(":") + 1).Replace("}", "").Trim();
									foreach(var ta in allAppsTenant)
									{
										if(ta.Title.Equals(appName, StringComparison.OrdinalIgnoreCase))
										{
											appId = ta.Id;
											break;
										}
									}
								}
								var alreadyExists = siteApps.Any(a => a.Id == appId);
								if(!alreadyExists)
								{
									// Check site collection app catalog
									alreadyExists = siteAppsSiteCollection.Any(a => a.Id == appId);
								}

                                var working = false;

                                if (app.Action == AppAction.Install && !alreadyExists)
                                {
									manager.Install(appId, GetAppScope(appId));
									working = true;
                                }
                                else if (app.Action == AppAction.Install && alreadyExists)
                                {
                                    WriteMessage($"App with ID {appId} already exists in the target site and will be skipped", ProvisioningMessageType.Warning);
                                }
                                else if (app.Action == AppAction.Uninstall && alreadyExists)
                                {
                                    manager.Uninstall(appId, GetAppScope(appId));
                                    working = true;
                                }
                                else if (app.Action == AppAction.Uninstall && !alreadyExists)
                                {
                                    WriteMessage($"App with ID {appId} does not exist in the target site and cannot be uninstalled", ProvisioningMessageType.Warning);
                                }
                                else if (app.Action == AppAction.Update && alreadyExists)
                                {
                                    manager.Upgrade(appId, GetAppScope(appId));
                                    working = true;
                                }
                                else if (app.Action == AppAction.Update && !alreadyExists)
                                {
                                    WriteMessage($"App with ID {appId} does not exist in the target site and cannot be updated", ProvisioningMessageType.Warning);
                                }

                                if (app.SyncMode == SyncMode.Synchronously && working)
                                {
                                    // We need to wait for the app management
                                    // to be completed before proceeding
                                    switch (app.Action)
                                    {
                                        case AppAction.Install:
                                        case AppAction.Update:
                                            {
                                                PollforAppInstalled(manager, appId, GetAppScope(appId));
                                                break;
                                            }
                                        case AppAction.Uninstall:
                                            {
                                                PollforAppUninstalled(manager, appId);
                                                break;
                                            }
                                    }
                                }
                            }
                        }
                        else
                        {
                            WriteMessage($"Tenant app catalog doesn't exist. ALM step will be skipped.", ProvisioningMessageType.Warning);
                        }
                    }
                }
            }

            return parser;
        }

        private void PollforAppInstalled(AppManager manager, Guid appId, Enums.AppCatalogScope scope = Enums.AppCatalogScope.Tenant)
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();
            var appMetadata = manager.GetAvailable(appId, scope);
            while (appMetadata.AppCatalogVersion != appMetadata.InstalledVersion && sw.ElapsedMilliseconds < 1000 * 60 * 5)
            {
                System.Threading.Thread.Sleep(5000); // sleep 5 seconds and try again
                appMetadata = manager.GetAvailable(appId, scope);
            }
            if (appMetadata.AppCatalogVersion != appMetadata.InstalledVersion)
            {
                // We ran into a timeout
                throw new Exception("App Install timeout hit, could not determine installed state");
            }
            sw.Stop();
        }

        private void PollforAppUninstalled(AppManager manager, Guid appId)
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();
            var appMetadata = manager.GetAvailable(appId, Enums.AppCatalogScope.Tenant);
            while (appMetadata.InstalledVersion != null && sw.ElapsedMilliseconds < 1000 * 60 * 5)
            {
                System.Threading.Thread.Sleep(5000); // sleep 5 seconds and try again
                appMetadata = manager.GetAvailable(appId, Enums.AppCatalogScope.Tenant);
            }
            if (appMetadata.InstalledVersion != null)
            {
                throw new Exception("App Uninstall timeout hit, could not determine uninstalled state.");
            }
        }

        public override bool WillExtract(Web web, ProvisioningTemplate template, ProvisioningTemplateCreationInformation creationInfo)
        {
            return (!web.IsSubSite());
        }

        public override bool WillProvision(Web web, ProvisioningTemplate template, ProvisioningTemplateApplyingInformation applyingInformation)
        {
            if (!_willProvision.HasValue && template.ApplicationLifecycleManagement != null)
            {
                _willProvision = (template.ApplicationLifecycleManagement.AppCatalog?.Packages != null && 
                                template.ApplicationLifecycleManagement.AppCatalog?.Packages.Count > 0) ||
                                template.ApplicationLifecycleManagement.Apps.Count > 0;
            }
			//return (!web.IsSubSite() && _willProvision.Value);
			return (_willProvision.Value);
		}
    }
#endif
}
