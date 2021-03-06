﻿/**
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

namespace Kafka.Client.ZooKeeperIntegration
{
    using Kafka.Client.Utils;
    using Kafka.Client.ZooKeeperIntegration.Events;
    using Kafka.Client.ZooKeeperIntegration.Listeners;
    using Org.Apache.Zookeeper.Proto;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using ZooKeeperNet;

    public partial class ZooKeeperClient
    {
        public static log4net.ILog Logger = log4net.LogManager.GetLogger(typeof(ZooKeeperClient));

        /// <summary>
        /// Represents the method that will handle a ZooKeeper event  
        /// </summary>
        /// <param name="args">
        /// The args.
        /// </param>
        /// <typeparam name="T">
        /// Type of event data
        /// </typeparam>
        public delegate void ZooKeeperEventHandler<T>(T args)
            where T : ZooKeeperEventArgs;

        /// <summary>
        /// Occurs when ZooKeeper connection state changes
        /// </summary>
        public event ZooKeeperEventHandler<ZooKeeperStateChangedEventArgs> StateChanged
        {
            add
            {
                this.EnsuresNotDisposed();
                lock (this.eventLock)
                {
                    this.stateChangedHandlers -= value;
                    this.stateChangedHandlers += value;
                }
            }
            remove
            {
                this.EnsuresNotDisposed();
                lock (this.eventLock)
                {
                    this.stateChangedHandlers -= value;
                }
            }
        }

        /// <summary>
        /// Occurs when ZooKeeper session re-creates
        /// </summary>
        public event ZooKeeperEventHandler<ZooKeeperSessionCreatedEventArgs> SessionCreated
        {
            add
            {
                this.EnsuresNotDisposed();
                lock (this.eventLock)
                {
                    this.sessionCreatedHandlers -= value;
                    this.sessionCreatedHandlers += value;
                }
            }
            remove
            {
                this.EnsuresNotDisposed();
                lock (this.eventLock)
                {
                    this.sessionCreatedHandlers -= value;
                }
            }
        }

        private readonly ConcurrentQueue<ZooKeeperEventArgs> eventsQueue = new ConcurrentQueue<ZooKeeperEventArgs>();
        private readonly object eventLock = new object();
        private ZooKeeperEventHandler<ZooKeeperStateChangedEventArgs> stateChangedHandlers;
        private ZooKeeperEventHandler<ZooKeeperSessionCreatedEventArgs> sessionCreatedHandlers;
        private Thread eventWorker;
        private Thread zooKeeperEventWorker;
        private readonly ConcurrentDictionary<string, ChildChangedEventItem> childChangedHandlers = new ConcurrentDictionary<string, ChildChangedEventItem>();
        private readonly ConcurrentDictionary<string, DataChangedEventItem> dataChangedHandlers = new ConcurrentDictionary<string, DataChangedEventItem>();
        private DateTime? idleTime;

        /// <summary>
        /// Gets time (in miliseconds) of event thread iddleness
        /// </summary>
        /// <remarks>
        /// Used for testing purpose
        /// </remarks>
        public int? IdleTime
        {
            get
            {
                return this.idleTime.HasValue ? Convert.ToInt32((DateTime.Now - this.idleTime.Value).TotalMilliseconds) : (int?)null;
            }
        }

        public void Process(WatchedEvent e)
        {
            Logger.DebugFormat("Process called by handler. Received event, e.EventType:{0}  e.State: {1} e.Path :{2} ", e.Type, e.State, e.Path);
            this.zooKeeperEventWorker = Thread.CurrentThread;
            if (this.shutdownTriggered)
            {
                Logger.DebugFormat("Shutdown triggered. Ignoring event. Type: {0}, Path: {1}, State: {2} ", e.Type, (e.Path ?? "null"), e.State);
                return;
            }

            try
            {
                this.EnsuresNotDisposed();
                bool stateChanged = string.IsNullOrEmpty(e.Path);
                bool znodeChanged = !string.IsNullOrEmpty(e.Path);
                bool dataChanged =
                    e.Type == EventType.NodeDataChanged
                    || e.Type == EventType.NodeDeleted
                    || e.Type == EventType.NodeCreated
                    || e.Type == EventType.NodeChildrenChanged;

                Logger.DebugFormat("Process called by handler. stateChanged:{0} znodeChanged:{1}  dataChanged:{2} ", stateChanged, znodeChanged, dataChanged);

                lock (this.somethingChanged)
                {
                    try
                    {
                        if (stateChanged)
                        {
                            this.ProcessStateChange(e);
                        }

                        if (dataChanged)
                        {
                            this.ProcessDataOrChildChange(e);
                        }
                    }
                    finally
                    {
                        if (stateChanged)
                        {
                            lock (this.stateChangedLock)
                            {
                                Monitor.PulseAll(this.stateChangedLock);
                            }

                            if (e.State == KeeperState.Expired)
                            {
                                lock (this.znodeChangedLock)
                                {
                                    Monitor.PulseAll(this.znodeChangedLock);
                                }

                                foreach (string path in this.childChangedHandlers.Keys)
                                {
                                    this.Enqueue(new ZooKeeperChildChangedEventArgs(path));
                                }

                                foreach (string path in this.dataChangedHandlers.Keys)
                                {
                                    this.Enqueue(new ZooKeeperDataChangedEventArgs(path));
                                }
                            }
                        }

                        if (znodeChanged)
                        {
                            lock (this.znodeChangedLock)
                            {
                                Monitor.PulseAll(this.znodeChangedLock);
                            }
                        }
                    }

                    Monitor.PulseAll(this.somethingChanged);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error occurred while processing event: " + ex.FormatException());
            }
        }

        /// <summary>
        /// Subscribes listeners on ZooKeeper state changes events
        /// </summary>
        /// <param name="listener">
        /// The listener.
        /// </param>
        public void Subscribe(IZooKeeperStateListener listener)
        {
            Guard.NotNull(listener, "listener");

            this.EnsuresNotDisposed();
            this.StateChanged += listener.HandleStateChanged;
            this.SessionCreated += listener.HandleSessionCreated;
            Logger.Debug("Subscribed state changes handler " + listener.GetType().Name);
        }

        /// <summary>
        /// Un-subscribes listeners on ZooKeeper state changes events
        /// </summary>
        /// <param name="listener">
        /// The listener.
        /// </param>
        public void Unsubscribe(IZooKeeperStateListener listener)
        {
            Guard.NotNull(listener, "listener");

            this.EnsuresNotDisposed();
            this.StateChanged -= listener.HandleStateChanged;
            this.SessionCreated -= listener.HandleSessionCreated;
            Logger.Debug("Unsubscribed state changes handler " + listener.GetType().Name);
        }

        /// <summary>
        /// Subscribes listeners on ZooKeeper child changes under given path
        /// </summary>
        /// <param name="path">
        /// The parent path.
        /// </param>
        /// <param name="listener">
        /// The listener.
        /// </param>
        public void Subscribe(string path, IZooKeeperChildListener listener)
        {
            Guard.NotNullNorEmpty(path, "path");
            Guard.NotNull(listener, "listener");

            this.EnsuresNotDisposed();
            this.childChangedHandlers.AddOrUpdate(
                path,
                new ChildChangedEventItem(listener.HandleChildChange),
                (key, oldValue) => { oldValue.ChildChanged += listener.HandleChildChange; return oldValue; });
            this.WatchForChilds(path);
            Logger.Debug("Subscribed child changes handler " + listener.GetType().Name + " for path: " + path);
        }

        /// <summary>
        /// Un-subscribes listeners on ZooKeeper child changes under given path
        /// </summary>
        /// <param name="path">
        /// The parent path.
        /// </param>
        /// <param name="listener">
        /// The listener.
        /// </param>
        public void Unsubscribe(string path, IZooKeeperChildListener listener)
        {
            Guard.NotNullNorEmpty(path, "path");
            Guard.NotNull(listener, "listener");

            this.EnsuresNotDisposed();
            this.childChangedHandlers.AddOrUpdate(
                path,
                new ChildChangedEventItem(),
                (key, oldValue) => { oldValue.ChildChanged -= listener.HandleChildChange; return oldValue; });
            Logger.Debug("Unsubscribed child changes handler " + listener.GetType().Name + " for path: " + path);
        }

        /// <summary>
        /// Subscribes listeners on ZooKeeper data changes under given path
        /// </summary>
        /// <param name="path">
        /// The parent path.
        /// </param>
        /// <param name="listener">
        /// The listener.
        /// </param>
        public void Subscribe(string path, IZooKeeperDataListener listener)
        {
            Guard.NotNullNorEmpty(path, "path");
            Guard.NotNull(listener, "listener");

            this.EnsuresNotDisposed();
            this.dataChangedHandlers.AddOrUpdate(
                path,
                new DataChangedEventItem(listener.HandleDataChange, listener.HandleDataDelete),
                (key, oldValue) =>
                {
                    oldValue.DataChanged += listener.HandleDataChange;
                    oldValue.DataDeleted += listener.HandleDataDelete;
                    return oldValue;
                });
            this.WatchForData(path);
            Logger.Debug("Subscribed data changes handler " + listener.GetType().Name + " for path: " + path);
        }

        /// <summary>
        /// Un-subscribes listeners on ZooKeeper data changes under given path
        /// </summary>
        /// <param name="path">
        /// The parent path.
        /// </param>
        /// <param name="listener">
        /// The listener.
        /// </param>
        public void Unsubscribe(string path, IZooKeeperDataListener listener)
        {
            Guard.NotNullNorEmpty(path, "path");
            Guard.NotNull(listener, "listener");

            this.EnsuresNotDisposed();
            this.dataChangedHandlers.AddOrUpdate(
                path,
                new DataChangedEventItem(),
                (key, oldValue) =>
                {
                    oldValue.DataChanged -= listener.HandleDataChange;
                    oldValue.DataDeleted -= listener.HandleDataDelete;
                    return oldValue;
                });
            Logger.Debug("Unsubscribed data changes handler " + listener.GetType().Name + " for path: " + path);
        }

        /// <summary>
        /// Un-subscribes all listeners
        /// </summary>
        public void UnsubscribeAll()
        {
            this.EnsuresNotDisposed();
            lock (this.eventLock)
            {
                this.stateChangedHandlers = null;
                this.sessionCreatedHandlers = null;
                this.childChangedHandlers.Clear();
                this.dataChangedHandlers.Clear();
            }

            Logger.Debug("Unsubscribed all handlers");
        }

        /// <summary>
        /// Installs a child watch for the given path. 
        /// </summary>
        /// <param name="path">
        ///     The parent path.
        /// </param>
        /// <returns>
        /// the current children of the path or null if the znode with the given path doesn't exist
        /// </returns>
        public IEnumerable<string> WatchForChilds(string path)
        {
            Guard.NotNullNorEmpty(path, "path");

            this.EnsuresNotDisposed();
            if (this.zooKeeperEventWorker != null && Thread.CurrentThread == this.zooKeeperEventWorker)
            {
                throw new InvalidOperationException("Must not be done in the zookeeper event thread.");
            }

            return this.RetryUntilConnected(
                () =>
                {
                    this.Exists(path);
                    try
                    {
                        return this.GetChildren(path);
                    }
                    catch (KeeperException e)
                    {
                        if (e.ErrorCode == KeeperException.Code.NONODE)
                            return null;
                        else
                            throw;
                    }
                });
        }

        /// <summary>
        /// Installs a data watch for the given path. 
        /// </summary>
        /// <param name="path">
        /// The parent path.
        /// </param>
        public void WatchForData(string path)
        {
            Guard.NotNullNorEmpty(path, "path");

            this.EnsuresNotDisposed();
            this.RetryUntilConnected(
                () => this.Exists(path, true));
        }

        /// <summary>
        /// Checks whether any data or child listeners are registered
        /// </summary>
        /// <param name="path">
        /// The path.
        /// </param>
        /// <returns>
        /// Value indicates whether any data or child listeners are registered
        /// </returns>
        private bool HasListeners(string path)
        {
            ChildChangedEventItem childChanged;
            this.childChangedHandlers.TryGetValue(path, out childChanged);
            if (childChanged != null && childChanged.Count > 0)
            {
                return true;
            }

            DataChangedEventItem dataChanged;
            this.dataChangedHandlers.TryGetValue(path, out dataChanged);
            if (dataChanged != null && dataChanged.TotalCount > 0)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Event thread starting method
        /// </summary>
        private void RunEventWorker()
        {
            Logger.Debug("Starting ZooKeeper watcher event thread");
            try
            {
                this.PoolEventsQueue();
            }
            catch (ThreadInterruptedException)
            {
                Logger.Debug("Terminate ZooKeeper watcher event thread");
            }
        }

        /// <summary>
        /// Pools ZooKeeper events form events queue
        /// </summary>
        /// <remarks>
        /// Thread sleeps if queue is empty
        /// </remarks>
        private void PoolEventsQueue()
        {
            while (true)
            {
                while (!this.eventsQueue.IsEmpty)
                {
                    this.Dequeue();
                }

                lock (this.somethingChanged)
                {
                    Logger.Debug("Awaiting events ...");
                    this.idleTime = DateTime.Now;
                    Monitor.Wait(this.somethingChanged);
                    this.idleTime = null;
                }
            }
        }

        /// <summary>
        /// Enqueues new event from ZooKeeper in events queue
        /// </summary>
        /// <param name="e">
        /// The event from ZooKeeper.
        /// </param>
        private void Enqueue(ZooKeeperEventArgs e)
        {
            Logger.Debug("New event queued: " + e);
            this.eventsQueue.Enqueue(e);
        }

        /// <summary>
        /// Dequeues event from events queue and invokes subscribed handlers
        /// </summary>
        private void Dequeue()
        {
            try
            {
                ZooKeeperEventArgs e;
                var success = this.eventsQueue.TryDequeue(out e);
                if (success)
                {
                    if (e != null)
                    {
                        Logger.Debug("Event dequeued: " + e);
                        switch (e.Type)
                        {
                            case ZooKeeperEventTypes.StateChanged:
                                this.OnStateChanged((ZooKeeperStateChangedEventArgs)e);
                                break;
                            case ZooKeeperEventTypes.SessionCreated:
                                this.OnSessionCreated((ZooKeeperSessionCreatedEventArgs)e);
                                break;
                            case ZooKeeperEventTypes.ChildChanged:
                                this.OnChildChanged((ZooKeeperChildChangedEventArgs)e);
                                break;
                            case ZooKeeperEventTypes.DataChanged:
                                this.OnDataChanged((ZooKeeperDataChangedEventArgs)e);
                                break;
                            default:
                                throw new InvalidOperationException("Not supported event type");
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                Logger.WarnFormat("Error handling event  {0}", exc.FormatException());
            }
        }

        /// <summary>
        /// Processess ZooKeeper state changes events
        /// </summary>
        /// <param name="e">
        /// The event data.
        /// </param>
        private void ProcessStateChange(WatchedEvent e)
        {
            Logger.Info("ProcessStateChange==zookeeper state changed (" + e.State + ")");
            lock (this.stateChangedLock)
            {
                Logger.InfoFormat("Current state:{0} in the lib:{1}", this.currentState, this.connection.GetInternalZKClient().State);
                this.currentState = e.State;
            }

            if (this.shutdownTriggered)
            {
                return;
            }

            this.Enqueue(new ZooKeeperStateChangedEventArgs(e.State));
            if (e.State == KeeperState.Expired)
            {
                while (true)
                {
                    try
                    {
                        this.Reconnect(this.connection.Servers, this.connection.SessionTimeout);
                        this.Enqueue(ZooKeeperSessionCreatedEventArgs.Empty);
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Exception occurred while trying to reconnect to ZooKeeper", ex);
                        Thread.Sleep(1000);
                        continue;
                    }
                }
            }
        }

        /// <summary>
        /// Processess ZooKeeper childs or data changes events
        /// </summary>
        /// <param name="e">
        /// The event data.
        /// </param>
        private void ProcessDataOrChildChange(WatchedEvent e)
        {
            if (this.shutdownTriggered)
            {
                return;
            }

            if (e.Type == EventType.NodeChildrenChanged
                || e.Type == EventType.NodeCreated
                || e.Type == EventType.NodeDeleted)
            {
                this.Enqueue(new ZooKeeperChildChangedEventArgs(e.Path));
            }

            if (e.Type == EventType.NodeDataChanged
                || e.Type == EventType.NodeCreated
                || e.Type == EventType.NodeDeleted)
            {
                this.Enqueue(new ZooKeeperDataChangedEventArgs(e.Path));
            }
        }

        /// <summary>
        /// Invokes subscribed handlers for ZooKeeeper state changes event
        /// </summary>
        /// <param name="e">
        /// The event data
        /// </param>
        private void OnStateChanged(ZooKeeperStateChangedEventArgs e)
        {
            try
            {
                var handlers = this.stateChangedHandlers;
                if (handlers == null)
                {
                    return;
                }

                foreach (var handler in handlers.GetInvocationList())
                {
                    Logger.Debug(e + " sent to " + handler.Target);
                }

                handlers(e);
            }
            catch (Exception exc)
            {
                Logger.ErrorFormat("Failed to handle state changed event.  {0}", exc.FormatException());
            }
        }

        /// <summary>
        /// Invokes subscribed handlers for ZooKeeeper session re-creates event
        /// </summary>
        /// <param name="e">
        /// The event data.
        /// </param>
        private void OnSessionCreated(ZooKeeperSessionCreatedEventArgs e)
        {
            var handlers = this.sessionCreatedHandlers;
            if (handlers == null)
            {
                return;
            }

            foreach (var handler in handlers.GetInvocationList())
            {
                Logger.Debug(e + " sent to " + handler.Target);
            }

            handlers(e);
        }

        /// <summary>
        /// Invokes subscribed handlers for ZooKeeeper child changes event
        /// </summary>
        /// <param name="e">
        /// The event data.
        /// </param>
        private void OnChildChanged(ZooKeeperChildChangedEventArgs e)
        {
            ChildChangedEventItem handlers;
            this.childChangedHandlers.TryGetValue(e.Path, out handlers);
            if (handlers == null || handlers.Count == 0)
            {
                return;
            }

            this.Exists(e.Path);
            try
            {
                IEnumerable<string> children = this.GetChildren(e.Path);
                e.Children = children;
            }
            catch (KeeperException ex)// KeeperException.NoNodeException)
            {
                if (ex.ErrorCode == KeeperException.Code.NONODE)
                {

                }
                else
                    throw;
            }

            handlers.OnChildChanged(e);
        }

        /// <summary>
        /// Invokes subscribed handlers for ZooKeeeper data changes event
        /// </summary>
        /// <param name="e">
        /// The event data.
        /// </param>
        private void OnDataChanged(ZooKeeperDataChangedEventArgs e)
        {
            DataChangedEventItem handlers;
            this.dataChangedHandlers.TryGetValue(e.Path, out handlers);
            if (handlers == null || handlers.TotalCount == 0)
            {
                return;
            }

            try
            {
                this.Exists(e.Path, true);
                var data = this.ReadData<string>(e.Path, null, true);
                e.Data = data;
                handlers.OnDataChanged(e);
            }
            catch (KeeperException ex)
            {
                if (ex.ErrorCode == KeeperException.Code.NONODE)
                    handlers.OnDataDeleted(e);
                else
                    throw;
            }
        }
    }
}
