﻿/*
 * Radegast Metaverse Client
 * Copyright(c) 2009-2014, Radegast Development Team
 * Copyright(c) 2016-2021, Sjofn, LLC
 * All rights reserved.
 *  
 * Radegast is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program.If not, see<https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Radegast
{
    #region enums
    /// <summary>
    /// Enum representing different modes of handling display names
    /// </summary>
    public enum NameMode
    {
        /// <summary> No display names </summary>
        Standard,
        /// <summary> Display name followed by (username) if display name is not default  </summary>
        Smart,
        /// <summary> Display name followed by (username) </summary>
        OnlyDisplayName,
        /// <summary> Only display </summary>
        DisplayNameAndUserName,
    }
    #endregion enums

    /// <summary>
    /// Manager for looking up avatar names and their caching 
    /// </summary>
    public class NameManager : IDisposable
    {
        #region public fields and properties
        public event EventHandler<UUIDNameReplyEventArgs> NameUpdated;

        public NameMode Mode
        {
            get => !client.Avatars.DisplayNamesAvailable() 
                ? NameMode.Standard
                : (NameMode)instance.GlobalSettings["display_name_mode"].AsInteger();

            set => instance.GlobalSettings["display_name_mode"] = (int)value;
        }
        #endregion public fields and properties

        #region private fields and properties

        private readonly RadegastInstance instance;
        private GridClient client => instance.Client;
        private Timer requestTimer;
        private Timer cacheTimer;
        private string cacheFileName;

        private readonly Queue<UUID> requests = new Queue<UUID>();

        private readonly int MaxNameRequests = 80;

        // Queue up name request for this many ms, and send a batch request
        private const int REQUEST_DELAY = 100;
        // Save name cache after change to names after this many ms
        private const int CACHE_DELAY = 30000;
        // Consider request failed after this many ms
        private const int MAX_REQ_AGE = 15000;

        private readonly DateTime UUIDNameOnly = new DateTime(1970, 9, 4, 10, 0, 0, DateTimeKind.Utc);

        private readonly Dictionary<UUID, AgentDisplayName> names = new Dictionary<UUID, AgentDisplayName>();
        private readonly Dictionary<UUID, int> activeRequests = new Dictionary<UUID, int>();

        #endregion private fields and properties

        #region construction and disposal
        public NameManager(RadegastInstance instance)
        {
            this.instance = instance;

            requestTimer = new Timer(MakeRequest, null, Timeout.Infinite, Timeout.Infinite);
            cacheTimer = new Timer(SaveCache, null, Timeout.Infinite, Timeout.Infinite);
            cacheFileName = Path.Combine(instance.UserDir, "name.cache");
            LoadCachedNames();
            instance.ClientChanged += new EventHandler<ClientChangedEventArgs>(instance_ClientChanged);
            RegisterEvents(client);
        }

        public void Dispose()
        {
            SaveCache(new object());

            if (client != null)
            {
                DeregisterEvents(client);
            }

            if (requestTimer != null)
            {
                requestTimer.Dispose();
                requestTimer = null;
            }

            if (cacheTimer != null)
            {
                cacheTimer.Dispose();
                cacheTimer = null;
            }
        }
        #endregion construction and disposal

        #region private methods

        void RegisterEvents(GridClient c)
        {
            c.Avatars.UUIDNameReply += new EventHandler<UUIDNameReplyEventArgs>(Avatars_UUIDNameReply);
            c.Avatars.DisplayNameUpdate += new EventHandler<DisplayNameUpdateEventArgs>(Avatars_DisplayNameUpdate);
        }

        void DeregisterEvents(GridClient c)
        {
            c.Avatars.UUIDNameReply -= new EventHandler<UUIDNameReplyEventArgs>(Avatars_UUIDNameReply);
            c.Avatars.DisplayNameUpdate -= new EventHandler<DisplayNameUpdateEventArgs>(Avatars_DisplayNameUpdate);
        }

        void instance_ClientChanged(object sender, ClientChangedEventArgs e)
        {
            DeregisterEvents(e.OldClient);
            RegisterEvents(e.Client);
        }

        void Avatars_DisplayNameUpdate(object sender, DisplayNameUpdateEventArgs e)
        {
            lock (names)
            {
                e.DisplayName.Updated = DateTime.Now;
                names[e.DisplayName.ID] = e.DisplayName;
            }

            var ret = new Dictionary<UUID, string>
                { {e.DisplayName.ID, FormatName(e.DisplayName)}};
            TriggerEvent(ret);
        }

        void Avatars_UUIDNameReply(object sender, UUIDNameReplyEventArgs e)
        {
            var ret = new Dictionary<UUID, string>();

            foreach (var kvp in e.Names)
            {
                // Remove from the list of active requests if in UUID only (standard mode)
                if (Mode == NameMode.Standard)
                {
                    lock (activeRequests)
                    {
                        activeRequests.Remove(kvp.Key);
                    }
                }

                lock (names)
                {
                    if (!names.ContainsKey(kvp.Key))
                    {
                        names[kvp.Key] = new AgentDisplayName
                        {
                            ID = kvp.Key,
                            NextUpdate = UUIDNameOnly,
                            IsDefaultDisplayName = true
                        };
                    }

                    names[kvp.Key].Updated = DateTime.Now;

                    var parts = kvp.Value.Trim().Split(' ');
                    if (parts.Length != 2) continue;
                    if (InvalidName(names[kvp.Key].DisplayName))
                    {
                        names[kvp.Key].DisplayName = $"{parts[0]} {parts[1]}";
                    }

                    names[kvp.Key].LegacyFirstName = parts[0];
                    names[kvp.Key].LegacyLastName = parts[1];
                    names[kvp.Key].UserName = names[kvp.Key].LegacyLastName == "Resident" 
                        ? names[kvp.Key].LegacyFirstName.ToLower() 
                        : $"{parts[0]}.{parts[1]}".ToLower();

                    ret.Add(kvp.Key, FormatName(names[kvp.Key]));
                }
            }

            TriggerEvent(ret);
            TriggerCacheSave();
        }

        string FormatName(AgentDisplayName n)
        {
            switch (Mode)
            {
                case NameMode.OnlyDisplayName:
                    return n.DisplayName;

                case NameMode.Smart:
                    return n.IsDefaultDisplayName ? n.DisplayName : $"{n.DisplayName} ({n.UserName})";

                case NameMode.DisplayNameAndUserName:
                    return $"{n.DisplayName} ({n.UserName})";

                case NameMode.Standard:
                default:
                    return n.LegacyFullName;
            }
        }

        bool InvalidName(string displayName)
        {
            return string.IsNullOrEmpty(displayName) ||
                   displayName == "???" ||
                   displayName == RadegastInstance.INCOMPLETE_NAME;
        }

        void ProcessDisplayNames(AgentDisplayName[] names)
        {
            var ret = new Dictionary<UUID, string>();

            foreach (var name in names)
            {
                // Remove from the list of active requests
                lock (activeRequests)
                {
                    activeRequests.Remove(name.ID);
                }

                if (InvalidName(name.DisplayName)) continue;

                ret.Add(name.ID, FormatName(name));

                name.Updated = DateTime.Now;
                lock (this.names)
                {
                    this.names[name.ID] = name;
                }
            }

            TriggerEvent(ret);
            TriggerCacheSave();
        }

        void MakeRequest(object sync)
        {
            var req = new List<UUID>();
            lock (requests)
            {
                while (requests.Count > 0)
                    req.Add(requests.Dequeue());
            }


            if (req.Count <= 0) { return; }
            if (Mode == NameMode.Standard || (!client.Avatars.DisplayNamesAvailable()))
            {
                client.Avatars.RequestAvatarNames(req);
            }
            else // Use display names
            {
                client.Avatars.GetDisplayNames(req, (success, names, badIDs) =>
                {
                    if (success)
                    {
                        ProcessDisplayNames(names);
                    }
                    else
                    {
                        Logger.Log("Failed fetching display names", Helpers.LogLevel.Warning, client);
                    }
                });
            }
        }

        void SaveCache(object sync)
        {
            ThreadPool.QueueUserWorkItem(syncx =>
            {
                OSDArray namesOSD = new OSDArray(names.Count);
                lock (names)
                {
                    foreach (var name in names)
                    {
                        namesOSD.Add(name.Value.GetOSD());
                    }
                }

                OSDMap cache = new OSDMap(1) {["names"] = namesOSD};
                byte[] data = OSDParser.SerializeLLSDBinary(cache, false);
                Logger.DebugLog($"Caching {namesOSD.Count} avatar names to {cacheFileName}");

                try
                {
                    File.WriteAllBytes(cacheFileName, data);
                }
                catch (Exception ex)
                {
                    Logger.Log("Failed to save avatar name cache: ", Helpers.LogLevel.Error, client, ex);
                }
            });
        }

        void LoadCachedNames()
        {
            ThreadPool.QueueUserWorkItem(syncx =>
            {
                try
                {
                    byte[] data = File.ReadAllBytes(cacheFileName);
                    OSDMap cache = (OSDMap)OSDParser.DeserializeLLSDBinary(data);
                    OSDArray namesOSD = (OSDArray)cache["names"];
                    DateTime now = DateTime.Now;
                    TimeSpan maxAge = new TimeSpan(48, 0, 0);
                    NameMode mode = (NameMode)(int)instance.GlobalSettings["display_name_mode"];

                    lock (names)
                    {
                        foreach (var osdname in namesOSD)
                        {
                            AgentDisplayName name = AgentDisplayName.FromOSD(osdname);
                            if (mode == NameMode.Standard || ((now - name.Updated) < maxAge))
                            {
                                names[name.ID] = name;
                            }
                        }
                    }

                    Logger.DebugLog($"Restored {names.Count} names from the avatar name cache");
                }
                catch (Exception ex)
                {
                    Logger.Log("Failed loading cached avatar names: ", Helpers.LogLevel.Warning, client, ex);
                }
            });
        }

        void TriggerEvent(Dictionary<UUID, string> ret)
        {
            if (NameUpdated == null || ret.Count == 0) return;

            try
            {
                NameUpdated(this, new UUIDNameReplyEventArgs(ret));
            }
            catch (Exception ex)
            {
                Logger.Log("Failure in event handler: " + ex.Message, Helpers.LogLevel.Warning, client, ex);
            }
        }

        void TriggerNameRequest()
        {
            requestTimer?.Change(REQUEST_DELAY, Timeout.Infinite);
        }

        void TriggerCacheSave()
        {
            cacheTimer?.Change(CACHE_DELAY, Timeout.Infinite);
        }

        void QueueNameRequest(UUID agentID)
        {
            // Check if we're already waiting for an answer on this one
            lock (activeRequests)
            {
                if (activeRequests.ContainsKey(agentID))
                {
                    if (Environment.TickCount - activeRequests[agentID] < MAX_REQ_AGE) // Not timeout yet
                    {
                        return;
                    }
                }

                // Record time of when we're making this request
                activeRequests[agentID] = Environment.TickCount;
            }


            lock (requests)
            {
                if (requests.Contains(agentID)) { return; }
                
                requests.Enqueue(agentID);

                if (requests.Count >= MaxNameRequests && Mode != NameMode.Standard)
                {
                    requestTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    MakeRequest(this);
                }
                else
                {
                    TriggerNameRequest();
                }
            }
        }
        #endregion private methods

        #region public methods
        /// <summary>
        /// Cleans avatar name cache
        /// </summary>
        public void CleanCache()
        {
            lock (names)
            {
                try
                {
                    names.Clear();
                    File.Delete(cacheFileName);
                }
                catch { }
            }
        }

        /// <summary>
        /// Gets legacy First Last name
        /// </summary>
        /// <param name="agentID">UUID of the agent</param>
        /// <returns></returns>
        public string GetLegacyName(UUID agentID)
        {
            if (agentID == UUID.Zero) { return "(???) (???)"; }

            lock (names)
            {
                if (names.ContainsKey(agentID))
                {
                    return names[agentID].LegacyFullName;
                }
            }

            QueueNameRequest(agentID);
            return RadegastInstance.INCOMPLETE_NAME;
        }

        /// <summary>
        /// Gets UserName
        /// </summary>
        /// <param name="agentID">UUID of the agent</param>
        /// <returns></returns>
        public string GetUserName(UUID agentID)
        {
            if (agentID == UUID.Zero) { return "(???) (???)"; }

            lock (names)
            {
                if (names.ContainsKey(agentID))
                {
                    return names[agentID].UserName;
                }
            }

            QueueNameRequest(agentID);
            return RadegastInstance.INCOMPLETE_NAME;
        }

        /// <summary>
        /// Gets DisplayName
        /// </summary>
        /// <param name="agentID">UUID of the agent</param>
        /// <returns></returns>
        public string GetDisplayName(UUID agentID)
        {
            if (agentID == UUID.Zero) { return "(???) (???)"; }

            lock (names)
            {
                if (names.ContainsKey(agentID))
                {
                    return names[agentID].DisplayName;
                }
            }

            QueueNameRequest(agentID);
            return RadegastInstance.INCOMPLETE_NAME;
        }

        /// <summary>
        /// Get avatar display name, or queue fetching of the name
        /// </summary>
        /// <param name="agentID">UUID of avatar to lookup</param>
        /// <returns>Avatar display name or "Loading..." if not in cache</returns>
        public string Get(UUID agentID)
        {
            if (agentID == UUID.Zero) { return "(???) (???)"; }

            string name = null;
            bool requestName = true;

            lock (names)
            {
                if (names.ContainsKey(agentID))
                {
                    if (Mode == NameMode.Standard || names[agentID].NextUpdate != UUIDNameOnly)
                    {
                        requestName = false;
                    }
                    name = FormatName(names[agentID]);
                }
            }

            if (requestName)
            {
                QueueNameRequest(agentID);
            }
            return string.IsNullOrEmpty(name) ? RadegastInstance.INCOMPLETE_NAME : name;
        }

        /// <summary>
        /// Get avatar display name, or queue fetching of the name
        /// </summary>
        /// <param name="agentID">UUID of avatar to lookup</param>
        /// <param name="blocking">If true, wait until name is received, otherwise return immediately</param>
        /// <returns>Avatar display name or "Loading..." if not in cache</returns>
        public string Get(UUID agentID, bool blocking)
        {
            if (!blocking)
                Get(agentID);

            string name = null;

            using (ManualResetEvent gotName = new ManualResetEvent(false))
            {

                EventHandler<UUIDNameReplyEventArgs> handler = (sender, e) =>
                {
                    if (e.Names.ContainsKey(agentID))
                    {
                        name = e.Names[agentID];
                        gotName.Set();
                    }
                };

                NameUpdated += handler;
                name = Get(agentID);

                if (name == RadegastInstance.INCOMPLETE_NAME)
                {
                    gotName.WaitOne(20 * 1000, false);
                }

                NameUpdated -= handler;
            }
            return name;
        }

        /// <summary>
        /// Get avatar display name, or queue fetching of the name
        /// </summary>
        /// <param name="agentID">UUID of avatar to lookup</param>
        /// <param name="defaultValue">If name failed to retrieve, use this</param>
        /// <returns>Avatar display name or the default value if not in cache</returns>
        public string Get(UUID agentID, string defaultValue)
        {
            if (Mode == NameMode.Standard)
                return defaultValue;

            string name = Get(agentID);
            return name == RadegastInstance.INCOMPLETE_NAME ? defaultValue : name;
        }

        /// <summary>
        /// Get avatar display name, or queue fetching of the name
        /// </summary>
        /// <param name="agentID">UUID of avatar to lookup</param>
        /// <param name="blocking">If true, wait until name is recieved, otherwise return immediately</param>
        /// <param name="defaultValue">If name failed to retrieve, use this</param>
        /// <returns></returns>
        public string Get(UUID agentID, bool blocking, string defaultValue)
        {
            if (Mode == NameMode.Standard)
                return defaultValue;

            string name = Get(agentID, blocking);
            return name == RadegastInstance.INCOMPLETE_NAME ? defaultValue : name;
        }

        #endregion public methods
    }
}
