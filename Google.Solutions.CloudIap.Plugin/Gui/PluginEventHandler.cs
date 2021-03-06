﻿//
// Copyright 2019 Google LLC
//
// Licensed to the Apache Software Foundation (ASF) under one
// or more contributor license agreements.  See the NOTICE file
// distributed with this work for additional information
// regarding copyright ownership.  The ASF licenses this file
// to you under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License.  You may obtain a copy of the License at
// 
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations
// under the License.
//

using Google.Solutions.CloudIap.Plugin.Configuration;
using Google.Solutions.CloudIap.Plugin.Integration;
using Google.Solutions.Compute;
using Google.Solutions.Compute.Auth;
using Google.Solutions.Compute.Iap;
using Google.Solutions.Compute.Net;
using RdcMan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Google.Solutions.CloudIap.Plugin.Gui
{
    internal class PluginEventHandler
    {
        private const int RemoteDesktopPort = 3389;

        private readonly PluginConfigurationStore configurationStore;
        private readonly TunnelManagerBase tunnelManager;
        private readonly IAuthorization authorization;

        private readonly Form mainForm;
        private readonly IPluginContext pluginContext;

        public PluginEventHandler(
            PluginConfigurationStore configurationStore,
            IAuthorization authorization,
            Form mainForm,
            IPluginContext pluginContext)
        {
            this.configurationStore = configurationStore;
            this.authorization = authorization;
            this.mainForm = mainForm;
            this.pluginContext = pluginContext;

            // N.B. Do not pre-create a ComputeEngineAdapter because the 
            // underlying ComputeService caches OAuth credentials, defying
            // re-auth.

            var configuration = configurationStore.Configuration;
            Compute.Compute.Trace.Listeners.Add(new DefaultTraceListener());
            Compute.Compute.Trace.Switch.Level = configuration.TracingLevel;

            this.tunnelManager = configuration.Tunneler == Tunneler.Gcloud
                ? (TunnelManagerBase)new GcloudTunnelManager(this.configurationStore)
                : (TunnelManagerBase)new DefaultTunnelingManager(authorization.Credential);

            ExtendMainMenu();
        }

        private void ExtendMainMenu()
        {
            // Add menu items.
            var addProjectMenuItem = new ToolStripMenuItem("Add &project...");
            addProjectMenuItem.Click += OnAddProjectClick;

            var configMenuItem = new ToolStripMenuItem("&Settings...");
            configMenuItem.Click += (sender, args) =>
            {
                var currentConfiguration = this.configurationStore.Configuration;
                if (ConfigurationDialog.ShowDialog(this.mainForm, currentConfiguration)
                    == DialogResult.OK)
                {
                    // Write back updated configuration
                    configurationStore.Configuration = currentConfiguration;
                }
            };

            var tunnelsMenuItem = new ToolStripMenuItem("Active &tunnels...");
            tunnelsMenuItem.Click += (sender, args) =>
            {
                TunnelsWindow.ShowDialog(this.mainForm, this.tunnelManager);
            };

            var signOutMenuItem = new ToolStripMenuItem("Sign &out");
            signOutMenuItem.Click += async (sender, args) =>
            {
                try
                {
                    await this.authorization.RevokeAsync();
                    MessageBox.Show(
                        this.mainForm,
                        "You will be prompted to sign in again once you restart the application.",
                        "Signed out",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception e)
                {
                    ExceptionUtil.HandleException(this.mainForm, "Sign out", e);
                }
            };

            var pluginMenuItem = new ToolStripMenuItem("Cloud &IAP");
            pluginMenuItem
                .DropDownItems
                .AddRange(new ToolStripItem[] {
                    addProjectMenuItem,
                    tunnelsMenuItem,
                    new ToolStripSeparator(),
                    configMenuItem,
                    signOutMenuItem,
                });

            this.pluginContext.MainForm.MainMenuStrip.Items.Insert(
                this.pluginContext.MainForm.MainMenuStrip.Items.Count - 1,
                pluginMenuItem);

            // Adjust states of Session > xxx menu items depending on selected server.
            this.pluginContext.MainForm.MainMenuStrip.MenuActivate += (sender, args) =>
            {
                // The plugin API does not provide access to the currently selected node
                // in the server tree, so we need to access an internal member to get hold
                // of this information.
                var selectedNode = (RdcTreeNode)mainForm.GetType().GetMethod(
                    "GetSelectedNode",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(
                        mainForm,
                        new object[0]);

                if (selectedNode is Server server)
                {
                    var sessionsMenuStrip = this.pluginContext.MainForm.MainMenuStrip.Items
                        .Cast<ToolStripMenuItem>()
                        .First(i => i.Name == "Session");

                    TweakMenuItemsForSelectedServer(
                        sessionsMenuStrip.DropDown.Items
                            .Cast<object>()
                            .Where(i => i is ToolStripMenuItem)
                            .Cast<ToolStripMenuItem>(),
                        server);
                }
            };
        }

        //---------------------------------------------------------------------
        // Plugin event handlers.
        //---------------------------------------------------------------------

        public void OnContextMenu(System.Windows.Forms.ContextMenuStrip contextMenuStrip, RdcTreeNode node)
        {
            // If node refers to a node in a virtual group like "Connected Servers",
            // perform a dereference first.
            if (node is ServerRef serverRef)
            {
                node = serverRef.ServerNode;
            }

            if (node is FileGroup fileGroup)
            {
                ToolStripMenuItem loadServers = new ToolStripMenuItem(
                    string.Format(
                        "{0} GCE &instances from {1}",
                        fileGroup.Nodes.Count == 0 ? "Add" : "Refresh",
                        node.Text), 
                    Resources.DownloadWebSetting.WithMagentaAsTransparent());
                loadServers.Click += (sender, args) => this.OnLoadServersClick(fileGroup);

                contextMenuStrip.Items.Insert(contextMenuStrip.Items.Count - 1, loadServers);
                contextMenuStrip.Items.Insert(contextMenuStrip.Items.Count - 1, new ToolStripSeparator());
            }
            else if (node is Group && node.Parent != null && node.Parent is FileGroup parentFileGroup)
            {
                ToolStripMenuItem loadServers = new ToolStripMenuItem(
                    $"Refresh GCE &instances from {parentFileGroup.Text}",
                    Resources.DownloadWebSetting.WithMagentaAsTransparent());
                loadServers.Click += (sender, args) => this.OnLoadServersClick(parentFileGroup);

                contextMenuStrip.Items.Insert(contextMenuStrip.Items.Count - 1, loadServers);
                contextMenuStrip.Items.Insert(contextMenuStrip.Items.Count - 1, new ToolStripSeparator());
            }
            else if (node is Server server && node.Parent != null && node.Parent is Group)
            {
                ToolStripMenuItem iapConnect = new ToolStripMenuItem(
                   $"Connect server via Cloud &IAP",
                   Resources.RemoteDesktop);
                iapConnect.Click += (sender, args) => OnIapConnectClick(server, false);
                iapConnect.Enabled = !server.IsConnected;

                ToolStripMenuItem iapConnectAs = new ToolStripMenuItem(
                   $"Connect server via Cloud &IAP as...",
                   Resources.RemoteDesktop);
                iapConnectAs.Click += (sender, args) => OnIapConnectClick(server, true);
                iapConnectAs.Enabled = !server.IsConnected;

                ToolStripMenuItem resetPassword = new ToolStripMenuItem(
                   $"Generate &Windows logon credentials...",
                   Resources.ChangePassword.WithMagentaAsTransparent());
                resetPassword.Click += (sender, args) => OnResetPasswordClick(server);

                ToolStripMenuItem showSerialPortOutput = new ToolStripMenuItem(
                   $"Show serial port &output...",
                   Resources.ActionLog.WithMagentaAsTransparent());
                showSerialPortOutput.Click += (sender, args) => OnShowSerialPortOutputClick(server);

                ToolStripMenuItem openCloudConsole = new ToolStripMenuItem(
                   $"Open in Cloud Consol&e...",
                   Resources.CloudConsole.ToBitmap());
                openCloudConsole.Click += (sender, args) => OnOpenCloudConsoleClick(server);

                ToolStripMenuItem openStackdriverLogs = new ToolStripMenuItem(
                   $"Show &Stackdriver logs...",
                   Resources.StackdriverLogging.ToBitmap());
                openStackdriverLogs.Click += (sender, args) => OnOpenStackdriverLogsClick(server);

                // Add custom context menu items.
                int index = 2;
                contextMenuStrip.Items.Insert(index++, new ToolStripSeparator());
                contextMenuStrip.Items.Insert(index++, iapConnect);
                contextMenuStrip.Items.Insert(index++, iapConnectAs);
                contextMenuStrip.Items.Insert(index++, resetPassword);
                contextMenuStrip.Items.Insert(index++, showSerialPortOutput);
                contextMenuStrip.Items.Insert(index++, openCloudConsole);
                contextMenuStrip.Items.Insert(index++, openStackdriverLogs);
                contextMenuStrip.Items.Insert(index++, new ToolStripSeparator());

                // Tweak existing context menu items.
                TweakMenuItemsForSelectedServer(
                    contextMenuStrip.Items
                        .Cast<object>()
                        .Where(i => i is ToolStripMenuItem)
                        .Cast<ToolStripMenuItem>(), 
                    server);
            }
        }

        private void TweakMenuItemsForSelectedServer(IEnumerable<ToolStripMenuItem> menuItems, Server server)
        {
            var connectedViaIap = this.tunnelManager.IsConnected(
                new TunnelDestination(
                    new VmInstanceReference(
                        server.FileGroup.Text,
                        server.Parent.Text,
                        server.DisplayName),
                    RemoteDesktopPort));

            // SessionConnect, SessionConnectAs, and SessionReconnect do not make sense if we are 
            // connected via IAP as they would cause a direct connection attempt.
            menuItems.First(i => i.Name == "SessionConnect").Enabled &= !connectedViaIap;
            menuItems.First(i => i.Name == "SessionConnectAs").Enabled &= !connectedViaIap;
            menuItems.First(i => i.Name == "SessionReconnect").Enabled &= !connectedViaIap;

            // SessionLogOff and SessionListSessions do not work over a tunnel. Clicking these
            // menu items would cause the action to be performed on the host machine.
            menuItems.First(i => i.Name == "SessionLogOff").Enabled &= !connectedViaIap;
            menuItems.First(i => i.Name == "SessionListSessions").Enabled &= !connectedViaIap;
        }

        public void Shutdown()
        {
            try
            {
                this.tunnelManager.CloseTunnels();
            }
            catch (Exception e)
            {
                // We are shutting down, so there is not too much we can do here.
                // It is pretty likely that some python.exe process will be leaked
                // though.
                Debug.WriteLine($"Failed to close tunnels: {e.Message}");
            }

            CheckForUpdates();
        }

        private void CheckForUpdates()
        {
            var currentConfiguration = this.configurationStore.Configuration;
            if (!currentConfiguration.CheckForUpdates)
            {
                return;
            }

            if ((DateTime.UtcNow - currentConfiguration.LastUpdateCheck).Days < 7)
            {
                // Last update check was less than a week ago - so back off.
                return;
            }

            // Determine latest available version.
            GithubAdapter.Release latestRelease;
            using (var cts = new CancellationTokenSource())
            {
                // Do not delay the shutdown unnecessarily.
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                // When this code run, the main form is already hidden. With
                // no GUI shown, blocking the main thread is okay.
                latestRelease = GithubAdapter.FindLatestReleaseAsync(cts.Token).Result;
            }

            var installedVersion = this.configurationStore.Configuration.Version;

            if (latestRelease == null ||
                latestRelease.TagVersion.CompareTo(installedVersion) <= 0)
            {
                // Installed version is up to date.
                return;
            }

            // Prompt for upgrade.
            int selectedOption = UnsafeNativeMethods.ShowOptionsTaskDialog(
                this.mainForm,
                "Update available",
                "An update is available for the Cloud IAP plugin",
                "Would you like to download the update now?",
                $"Installed version: {installedVersion}\nAvailable version: {latestRelease.TagVersion}",
                new[]
                {
                    "Yes, download now",
                    "More information",     // Same as pressing 'OK'
                    "No, download later"    // Same as pressing 'Cancel'
                },
                "Do not check for updates again",
                out bool donotCheckForUpdatesAgain);

            // Update settings.
            var newConfiguration = this.configurationStore.Configuration;
            newConfiguration.CheckForUpdates = !donotCheckForUpdatesAgain;
            newConfiguration.LastUpdateCheck = DateTime.UtcNow;
            this.configurationStore.Configuration = newConfiguration;

            if (selectedOption == 2)
            {
                // Cancel.
                return;
            }

            using (var launchBrowser = new Process())
            {
                if (selectedOption == 0 && latestRelease.Assets.Any())
                {
                    launchBrowser.StartInfo.FileName = latestRelease.Assets.First().DownloadUrl;
                }
                else
                {
                    launchBrowser.StartInfo.FileName = latestRelease.HtmlUrl;
                }

                launchBrowser.StartInfo.UseShellExecute = true;
                launchBrowser.Start();
            }
        }

        //---------------------------------------------------------------------
        // Custom event handlers
        //---------------------------------------------------------------------

        private void OnAddProjectClick(object sender, EventArgs e)
        {
            // To make sure we have a valid auth session,
            // force a token refresh first.
            WaitDialog.RunWithDialog(
                this.mainForm,
                "Loading projects...",
                _ => this.authorization.Credential.GetAccessTokenForRequestAsync(),
                _ => {
                    // Show project picker. 
                    string projectId = projectId = ProjectPickerDialog.SelectProjectId(
                        ResourceManagerAdapter.Create(this.authorization.Credential),
                        this.mainForm);

                    if (projectId == null)
                    {
                        // Cancelled.
                        return;
                    }

                    // Check if project already loaded.
                    var treeView = (TreeView)this.pluginContext.Tree;
                    if (treeView.Nodes
                        .Cast<TreeNode>()
                        .Where(node => node.Text == projectId)
                        .Any())
                    {
                        MessageBox.Show(
                            this.mainForm,
                            $"The project {projectId} has already been added",
                            "Add project",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                        return;
                    }

                    // Create a new file group for the given project.
                    var folderPath = this.configurationStore.Configuration.AppDataLocation;
                    if (!Directory.Exists(folderPath))
                    {
                        Directory.CreateDirectory(folderPath);
                    }

                    var rdgFilePath = Path.Combine(folderPath, projectId + ".rdg");
                    if (File.Exists(rdgFilePath))
                    {
                        // If the file exist, we must not create a new FileGroup. Instead,
                        // the file must be opened properly. But there is no API for that.
                        if (MessageBox.Show(
                            this.mainForm,
                            "There is an existing .rdg file for this project. \n" +
                            "Do you want to overwrite this file?",
                            "Add project",
                            MessageBoxButtons.YesNoCancel,
                            MessageBoxIcon.Warning) == DialogResult.Yes)
                        {
                            // Delete and re-create.
                            File.Delete(rdgFilePath);
                        }
                        else
                        {
                            // Stop.
                            return;
                        }
                    }

                    // The plugin lacks an API to create new file nodes, so we have to call
                    // an internal API for that.
                    var constructor = typeof(FileGroup)
                        .GetConstructor(
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null,
                            new[] { typeof(string) },
                            null);
                    var fileGroup = (FileGroup)constructor
                        .Invoke(new[] { rdgFilePath });

                    // Hydrate the group.
                    OnLoadServersClick(fileGroup);

                    this.pluginContext.Tree.AddNode(fileGroup, this.pluginContext.Tree.RootNode);
                },
                this.authorization.ReauthorizeAsync);
        }

        private void OnLoadServersClick(FileGroup fileGroup)
        {
            var projectId = fileGroup.Text;

            Func<string, string> longZoneToShortZone = 
                (string zone) => zone.Substring(zone.LastIndexOf("/") + 1);

            WaitDialog.RunWithDialog(
                this.mainForm,
                "Loading instances...",
                async _ => {
                    try
                    {
                        return await ComputeEngineAdapter.Create(authorization.Credential)
                            .QueryInstancesAsync(projectId);
                    }
                    catch (GoogleApiException e) when (e.Error != null && e.Error.Code == 403)
                    {
                        throw new ApplicationException(
                            "You do not have sufficient acces to this project", e);
                    }
                },
                allInstances =>
                {
                    // Narrow the list down to Windows instances - there is no point 
                    // of adding Linux instanes to the list of servers.
                    var instances = allInstances.Where(i => ComputeEngineAdapter.IsWindowsInstance(i));

                    // Consolidate zones.
                    var currentZones = fileGroup.Nodes
                        .Cast<RdcTreeNode>()
                        .Where(n => n is Group)
                        .Cast<Group>().Select(s => s.Text).ToHashSet();
                    var cloudZones = instances.Select(i => longZoneToShortZone(i.Zone)).ToHashSet();

                    var missingZones = cloudZones.Subtract(currentZones);
                    var junkZones = currentZones.Subtract(cloudZones);

                    foreach (var zone in junkZones)
                    {
                        fileGroup.Nodes.Remove(fileGroup.Nodes.Cast<Group>().First(s => s.Text == zone));
                    }

                    foreach (var zone in missingZones)
                    {
                        Group.Create(zone, fileGroup);
                    }

                    fileGroup.Expand();

                    // Consolidate instances per zone.
                    foreach (var zone in cloudZones)
                    {
                        var zoneGroup = fileGroup.Nodes
                            .Cast<RdcTreeNode>()
                            .Where(n => n is Group)
                            .Cast<Group>().First(g => g.Text == zone);

                        var currentServersInZone = zoneGroup.Nodes
                            .Cast<RdcTreeNode>()
                            .Where(n => n is Server)
                            .Cast<Server>().Select(s => s.DisplayName).ToHashSet();
                        var cloudServersInZone = instances
                            .Where(i => longZoneToShortZone(i.Zone) == zone)
                            .Select(i => i.Name).ToHashSet();

                        var missingServersInZone = cloudServersInZone.Subtract(currentServersInZone);
                        var junkServersInZone = currentServersInZone.Subtract(cloudServersInZone);

                        foreach (var server in junkServersInZone)
                        {
                            zoneGroup.Nodes.Remove(
                                zoneGroup.Nodes.Cast<Server>().First(s => s.DisplayName == server));
                        }

                        foreach (var server in missingServersInZone)
                        {
                            var instance = instances.First(i => i.Name == server);
                            Server.Create(instance.Name, instance.Name, zoneGroup);
                        }

                        zoneGroup.Expand();
                    }
                },
                this.authorization.ReauthorizeAsync);
        }

        private void OnIapConnectClick(Server server, bool connectAs)
        {
            var instanceRef = new VmInstanceReference(
                server.FileGroup.Text,
                server.Parent.Text,
                server.DisplayName);

            // Silly workaround for an RDCMan bug: When the server tree is set to
            // auto-hide and you trigger this action, the WM_KILLFOCUS message is not
            // handled properly by the respective ServerLabel. By activating the 
            // server tree, we prevent that from happening (at the expense of 
            // having the server tree briefly pop up).
            try
            {
                mainForm.GetType().GetMethod(
                    "GoToServerTree",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(
                        this.mainForm,
                        new object[0]);
            }
            catch (Exception)
            {
            }

            WaitDialog.RunWithDialog(
                this.mainForm,
                "Opening Cloud IAP tunnel...",
                async _ => {
                    try
                    {
                        return await this.tunnelManager.ConnectAsync(new TunnelDestination(
                            instanceRef,
                            RemoteDesktopPort),
                            this.configurationStore.Configuration.IapConnectionTimeout);
                    }
                    catch (NetworkStreamClosedException e)
                    {
                        throw new ApplicationException(
                            "Connecting to the instance failed. Make sure that you have "+
                            "configured your firewall rules to permit Cloud IAP access " +
                            $"to {instanceRef.InstanceName}", 
                            e);
                    }
                    catch (UnauthorizedException)
                    {
                        // Throw a more precise exception if this is because the instance
                        // is not running.
                        await VerifyInstanceIsRunning(instanceRef);

                        // Instance exists and is running, so it must be a genuine permission
                        // problem.
                        throw new ApplicationException("You are not authorized to connect to this VM instance");
                    }
                },
                tunnel =>
                {
                    var originalInheritMode = server.ConnectionSettings.InheritSettingsType.Mode;
                    var originalServerName = server.Properties.ServerName.Value;
                    var originalServerPort = server.ConnectionSettings.Port.Value;

                    try
                    {
                        server.ConnectionSettings.InheritSettingsType.Mode = InheritanceMode.None;
                        server.Properties.ServerName.Value = "localhost";
                        server.ConnectionSettings.Port.Value = tunnel.LocalPort;

                        // Set focus on selected server and connect.
                        server.TreeView.SelectedNode = server;
                        if (connectAs)
                        {
                            server.DoConnectAs();
                        }
                        else
                        {
                            server.Connect();
                        }
                    }
                    finally
                    {
                        // Restore original settings in case the user later wants to
                        // connect directly again.
                        server.Properties.ServerName.Value = originalServerName;
                        server.ConnectionSettings.Port.Value = originalServerPort;
                        server.ConnectionSettings.InheritSettingsType.Mode = originalInheritMode;
                    }
                },
                this.authorization.ReauthorizeAsync);
        }

        private void OnResetPasswordClick(Server server)
        {
            // Derive a suggested username from the Windows login name.
            var suggestedUsername = Environment.UserName;

            // Prompt for username to use.
            var username = GenerateCredentialsDialog.PromptForUsername(this.mainForm, suggestedUsername);
            if (username == null)
            {
                return;
            }

            VmInstanceReference instanceRef = new VmInstanceReference(
                server.FileGroup.Text,
                server.Parent.Text,
                server.DisplayName);
            
            WaitDialog.RunWithDialog(
                this.mainForm,
                "Generating Windows logon credentials...",
                async token => {
                    try
                    {
                        return await ComputeEngineAdapter.Create(authorization.Credential)
                            .ResetWindowsUserAsync(instanceRef, username, token);
                    }
                    catch (GoogleApiException)
                    {
                        // Throw a more precise exception if this is because the instance
                        // is not running.
                        await VerifyInstanceIsRunning(instanceRef);

                        // Must be some other problem.
                        throw;
                    }
                },
                credentials =>
                {
                    ShowCredentialsDialog.ShowDialog(
                        this.mainForm,
                        credentials.UserName,
                        credentials.Password);

                    server.LogonCredentials.InheritSettingsType.Mode = InheritanceMode.None;
                    server.LogonCredentials.Domain.Value = "localhost";
                    server.LogonCredentials.UserName.Value = credentials.UserName;
                    server.LogonCredentials.SetPassword(credentials.Password);
                },
                this.authorization.ReauthorizeAsync);
        }

        private void OnShowSerialPortOutputClick(Server server)
        {
            var instanceRef = new VmInstanceReference(
                server.FileGroup.Text,
                server.Parent.Text,
                server.DisplayName);

            // TODO: Handle reauth.
            SerialPortOutputWindow.Show(
                this.mainForm,
                $"{instanceRef.InstanceName} ({instanceRef.Zone})",
                ComputeEngineAdapter.Create(authorization.Credential).GetSerialPortOutput(instanceRef));
        }

        private void OnOpenCloudConsoleClick(Server server)
        {
            VmInstanceReference instance = new VmInstanceReference(
                server.FileGroup.Text,
                server.Parent.Text,
                server.DisplayName);

            Process.Start(new ProcessStartInfo()
            {
                UseShellExecute = true,
                Verb = "open",
                FileName = "https://console.cloud.google.com/compute/instancesDetail/zones/" +
                          $"{instance.Zone}/instances/{instance.InstanceName}?project={instance.ProjectId}"
            });
        }

        private void OnOpenStackdriverLogsClick(Server server)
        {
            VmInstanceReference instance = new VmInstanceReference(
                    server.FileGroup.Text,
                    server.Parent.Text,
                    server.DisplayName);

            WaitDialog.RunWithDialog(
                this.mainForm,
                "Loading instance information...",
                _ => ComputeEngineAdapter.Create(authorization.Credential).QueryInstanceAsync(instance),
                instanceDetails =>
                {
                    Process.Start(new ProcessStartInfo()
                    {
                        UseShellExecute = true,
                        Verb = "open",
                        FileName = "https://console.cloud.google.com/logs/viewer?" +
                              $"resource=gce_instance%2Finstance_id%2F{instanceDetails.Id}&project={instance.ProjectId}"
                    });
                },
                this.authorization.ReauthorizeAsync);
        }

        private async Task VerifyInstanceIsRunning(VmInstanceReference instanceRef)
        {
            try
            {
                var instance = await ComputeEngineAdapter.Create(authorization.Credential)
                    .QueryInstanceAsync(instanceRef);
                if (instance.Status != "RUNNING")
                {
                    throw new ApplicationException(
                        $"VM instance '{instanceRef.InstanceName}' is in {instance.Status} state");
                }
            }
            catch (GoogleApiException e) when (e.Error != null && e.Error.Code == 404)
            {
                throw new ApplicationException(
                    $"VM instance '{instanceRef.InstanceName}' does not exist or you lack permission to access it");
            }
        }
    }
}
