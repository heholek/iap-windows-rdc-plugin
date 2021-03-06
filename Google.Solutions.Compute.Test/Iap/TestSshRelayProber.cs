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

using Google.Apis.Auth.OAuth2;
using Google.Solutions.Compute.Iap;
using Google.Solutions.Compute.Net;
using Google.Solutions.Compute.Test.Env;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace Google.Solutions.Compute.Test.Iap
{
    [TestFixture]
    [Category("IntegrationTest")]
    [Category("IAP")]
    public class TestSshRelayProber : FixtureBase
    {
        [Test]
        public void ProbingNonexistingProjectCausesUnauthorizedException()
        {
            using (var stream = new SshRelayStream(
                new IapTunnelingEndpoint(
                    Defaults.GetCredential(),
                    new VmInstanceReference(
                        "invalid",
                        Defaults.Zone,
                        "invalid"),
                    80,
                    IapTunnelingEndpoint.DefaultNetworkInterface)))
            {
                AssertEx.ThrowsAggregateException<UnauthorizedException>(() =>
                    stream.TestConnectionAsync(TimeSpan.FromSeconds(10)).Wait());
            }
        }

        [Test]
        public void ProbingNonexistingZoneCausesUnauthorizedException()
        {
            using (var stream = new SshRelayStream(
               new IapTunnelingEndpoint(
                    Defaults.GetCredential(),
                    new VmInstanceReference(
                        Defaults.ProjectId,
                        "invalid",
                        "invalid"),
                    80,
                    IapTunnelingEndpoint.DefaultNetworkInterface)))
            {
                AssertEx.ThrowsAggregateException<UnauthorizedException>(() =>
                    stream.TestConnectionAsync(TimeSpan.FromSeconds(10)).Wait());
            }
        }

        [Test]
        public void ProbingNonexistingInstanceCausesUnauthorizedException()
        {
            using (var stream = new SshRelayStream(
                new IapTunnelingEndpoint(
                    Defaults.GetCredential(),
                    new VmInstanceReference(
                        Defaults.ProjectId,
                        Defaults.Zone,
                        "invalid"),
                    80,
                    IapTunnelingEndpoint.DefaultNetworkInterface)))
            {
                AssertEx.ThrowsAggregateException<UnauthorizedException>(() =>
                    stream.TestConnectionAsync(TimeSpan.FromSeconds(10)).Wait());
            }
        }

        [Test]
        public async Task ProbeExistingInstanceOverRdpSucceeds(
             [WindowsInstance] InstanceRequest testInstance)
        {
            await testInstance.AwaitReady();

            using (var stream = new SshRelayStream(
                new IapTunnelingEndpoint(
                    Defaults.GetCredential(),
                    testInstance.InstanceReference,
                    3389,
                    IapTunnelingEndpoint.DefaultNetworkInterface)))
            {
                await stream.TestConnectionAsync(TimeSpan.FromSeconds(10));
            }
        }

        [Test]
        public async Task ProbeExistingInstanceOverWrongCausesNetworkStreamClosedException(
             [WindowsInstance] InstanceRequest testInstance)
        {
            await testInstance.AwaitReady();

            using (var stream = new SshRelayStream(
                new IapTunnelingEndpoint(
                    Defaults.GetCredential(),
                    testInstance.InstanceReference,
                    22,
                    IapTunnelingEndpoint.DefaultNetworkInterface)))
            {
                AssertEx.ThrowsAggregateException<NetworkStreamClosedException>(() =>
                    stream.TestConnectionAsync(TimeSpan.FromSeconds(5)).Wait());
            }
        }
    }
}
