using System;
using System.Collections;
using Meebey.SmartIrc4net;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO;
using System.Data;
using Mono.Data.SqliteClient;
using log4net;

//Logging:
[assembly: log4net.Config.XmlConfigurator(Watch = true)]

namespace SWMTBot
{
    class QueuedMessage
    {
        public SendType type;
        public String destination;
        public String message;
        public long SentTime;
        public bool IsDroppable = false;
    }

    class Program
    {
        const string version = "1.20beta";

        public static IrcClient irc = new IrcClient();
        public static RCReader rcirc = new RCReader();
        public static ProjectList prjlist = new ProjectList();
        public static ListManager listman = new ListManager();
        public static SortedList msgs = new SortedList();
        public static SortedList mainConfig = new SortedList();
        private static ILog logger = LogManager.GetLogger("SWMTBot.Program");

        //Flood protection objects
        static Queue fcQueue = new Queue();
        static Queue priQueue = new Queue();
        static Boolean dontSendNow = false;
        static int sentLength = 0;
        static ManualResetEvent sendlock = new ManualResetEvent(true);

        static Regex broadcastMsg = new Regex(@"\*\x02B/1.1\x02\*(?<list>.+?)\*(?<action>.+?)\*\x03"
            +@"07\x02(?<item>.+?)\x02\x03\*\x03"
            +@"13(?<len>\d+?)\x03\*\x03"
            +@"09\x02(?<reason>.*?)\x02\x03\*\x03"
            +@"11\x02(?<adder>.*?)\x03\x02\*");
        static Regex botCmd;
        /* _1568: Added rcitems to .ini */
        static int editblank;
        static int editbig;
        static int newbig;
        static int newsmall;
        static bool ignoreBotEdits = true;
        static string ControlChannel;
        static string FeedChannel;
        static string BroadcastChannel;
        static string ircServerName;
        static int bufflen = 1400;
        static long maxlag = 600000000; // 60 seconds in 100-nanoseconds
        
        /**
         * Feed filters
         *
         * These settings allow filtering of user types and event types
         * They are defined via the .ini file and loaded on top of the Main thread
         * Possible values: 
         *  1 "show"     (show and allow autolist) - default
         *  2 "softhide" (hide non-specials, show exceptions and allow autolist)
         *  3 "hardhide" (hide all but do autolist)
         *  4 "ignore"   (hide and ignore totally)
         * show/ignore is dealt with at beginning of ReactToRCEvent()
         * hardhide is dealt with at end of ReactToRCEvent() (after autolistings are done)
         * softhide is done inline
         */
        static int feedFilterUsersAnon = 1;
        static int feedFilterUsersReg = 2;
        //TODO: static int feedFilterUsersBot;
        //TODO: static int feedFilterEventNewuser;
        static int feedFilterEventUpload = 1;
        
        // IsCubbie overrides feedfilters if true to only show uploads and ignore the rest
        static bool IsCubbie = false;
        
        // Set this to true to stops the bot from checking the database when requesting a usertype
        //  and instead will only return 3 (anon) or 4 (user) based on a regex.
        // This speeds up the the flow incredibly (especially when using SQLite) and makes it possible
        // to load a lot of the biggest wikis without any delay
        // This will mean that the actual output in the feedchannel is very unusable (all edits go through, no bot, user, whitelist detection)
        // Recommended to use in combination with high(est) feedFilter settings (originally written for CVNBlackRock bot)
        public static bool disableClassifyEditor = false;

        public static string botNick;

        static void Main(string[] args)
        {
            Thread.CurrentThread.Name = "Main";
            Thread.GetDomain().UnhandledException += new UnhandledExceptionEventHandler(Application_UnhandledException);

            string mainConfigFN = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                + Path.DirectorySeparatorChar + "SWMTBot.ini";

            logger.Info("Loading main configuration from "+mainConfigFN);
            using (StreamReader sr = new StreamReader(mainConfigFN))
            {
                String line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (!line.StartsWith("#") && (line != "")) //ignore comments
                    {
                        string[] parts = line.Split(new char[1] { '=' }, 2);
                        mainConfig.Add(parts[0], parts[1]);
                    }
                }
            }

            botNick = (string)mainConfig["botnick"];
            ControlChannel = (string)mainConfig["controlchannel"];
            FeedChannel = (string)mainConfig["feedchannel"];
            BroadcastChannel = (string)mainConfig["broadcastchannel"];
            ircServerName = (string)mainConfig["ircserver"];
            /* _1568: Added for .ini */
            editblank = Int32.Parse((string)mainConfig["editblank"]);
            editbig = Int32.Parse((string)mainConfig["editbig"]);
            newbig = Int32.Parse((string)mainConfig["newbig"]);
            newsmall = Int32.Parse((string)mainConfig["newsmall"]);
            prjlist.fnProjectsXML = (string)mainConfig["projects"];
            IsCubbie = mainConfig.ContainsKey("IsCubbie");
            disableClassifyEditor = mainConfig.ContainsKey("disableClassifyEditor");
            feedFilterUsersAnon = mainConfig.ContainsKey("feedFilterUsersAnon") ? Int32.Parse((string)mainConfig["feedFilterUsersAnon"]) : 1;
            feedFilterUsersReg = mainConfig.ContainsKey("feedFilterUsersReg") ? Int32.Parse((string)mainConfig["feedFilterUsersReg"]) : 2;
            feedFilterEventUpload = mainConfig.ContainsKey("feedFilterEventUpload") ? Int32.Parse((string)mainConfig["feedFilterEventUpload"]) : 1;

            botCmd = new Regex("^" + botNick + @" (\s*(?<command>\S*))(\s(?<params>.*))?$", RegexOptions.IgnoreCase);

            logger.Info("Loading messages");
            readMessages((string)mainConfig["messages"]);
            if ((!msgs.ContainsKey("00000")) || ((String)msgs["00000"] != "2.03"))
            {
                logger.Fatal("Message file version mismatch or read messages failed");
                Exit();
            }

            //Read projects (prjlist displays logger message)
            prjlist.loadFromFile();

            logger.Info("Loading lists");
            listman.initDBConnection((string)mainConfig["lists"]);

            logger.Info("Setting up main IRC client");
            //Set up freenode IRC client
            irc.Encoding = System.Text.Encoding.UTF8;
            irc.SendDelay = 300;
            //irc.AutoReconnect = true;
            //irc.AutoRejoin = true;
            irc.ActiveChannelSyncing = true;
            irc.OnChannelMessage += new IrcEventHandler(irc_OnChannelMessage);
            irc.OnChannelNotice += new IrcEventHandler(irc_OnChannelNotice);
            irc.OnConnected += new EventHandler(irc_OnConnected);
            irc.OnError += new Meebey.SmartIrc4net.ErrorEventHandler(irc_OnError);
            irc.OnConnectionError += new EventHandler(irc_OnConnectionError);
            irc.OnPong += new PongEventHandler(irc_OnPong);
            //irc.PingTimeout = 10;

            try
            {
                irc.Connect(ircServerName, 6667);
            }
            catch (ConnectionException e)
            {
                logger.Fatal("Could not connect: " + e.Message);
                Exit();
            }

            // Now initialize flood protection code
            new Thread(new ThreadStart(msgthread)).Start();

            try
            {
                irc.Login(botNick, (string)mainConfig["description"] + " " + version, 4, botNick, (string)mainConfig["botpass"]);
                irc.RfcJoin(ControlChannel);
                irc.RfcJoin(FeedChannel);
                if (BroadcastChannel != "None")
                    irc.RfcJoin(BroadcastChannel);

                //Now connect the RCReader to channels
                new Thread(new ThreadStart(rcirc.initiateConnection)).Start();

                // here we tell the IRC API to go into a receive mode, all events
                // will be triggered by _this_ thread (main thread in this case)
                // Listen() blocks by default, you can also use ListenOnce() if you
                // need that does one IRC operation and then returns, so you need then
                // an own loop
                irc.Listen();

                // when Listen() returns our IRC session is over, to be sure we call
                // disconnect manually
                irc.Disconnect();
            }
            catch (ConnectionException)
            {
                // this exception is handled because Disconnect() can throw a not
                // connected exception
                Exit();
            }
            catch (Exception e)
            {
                // this should not happen by just in case we handle it nicely
                logger.Fatal("Error occurred in Main IRC try clause! Message: " + e.Message);
                logger.Fatal("Exception: " + e.StackTrace);
                Exit();
            }
        }

        static void irc_OnConnectionError(object sender, EventArgs e)
        {
            //Let's try to catch those strange disposal errors
            //But only if it ain't a legitimate disconnection
            if (rcirc.rcirc.AutoReconnect)
            {
                logger.Error("OnConnectionError in Program, restarting...");
                Restart();
                //Exit(); /* DEBUG */
            }
        }

        /// <summary>
        /// Catches all unhandled exceptions in the main thread
        /// </summary>
        public static void Application_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                logger.Error("Caught unhandled exception in global catcher", (Exception)e.ExceptionObject);
            }
            catch
            {
                //Logging failed; considerably serious
                Console.WriteLine("Caught unhandled exception, and logging failed: " + ((Exception)e.ExceptionObject).ToString());

                try
                {
                    PartIRC("Caught unhandled exception and logging failed; restarting as a precaution");
                    Restart();
                }
                catch
                {
                    //Restart failed
                    Console.WriteLine("Restart failed; exiting with code 24.");
                    System.Environment.Exit(24);
                }
            }
        }

        static void irc_OnError(object sender, Meebey.SmartIrc4net.ErrorEventArgs e)
        {
            logger.Error("IRC: " + e.ErrorMessage);
            if (e.ErrorMessage.Contains("Excess Flood")) //Do not localize
            {
                //Oops, we were flooded off
                logger.Warn("Initiating restart sequence after Excess Flood");
                Restart();
            }
        }

        /// <summary>
        /// This event handler detects incoming broadcast messages
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void irc_OnChannelNotice(object sender, IrcEventArgs e)
        {
            if (e.Data.Channel != BroadcastChannel)
                return; //Just in case
            if (e.Data.Message == "" || e.Data.Message == null)
                return; //Prevent empty messages from crashing the bot
            Match bm = broadcastMsg.Match(e.Data.Message);
            if (bm.Success)
            {
                try
                {
                    string action = bm.Groups["action"].Captures[0].Value;
                    string list = bm.Groups["list"].Captures[0].Value;
                    string item = bm.Groups["item"].Captures[0].Value;
                    int len = Convert.ToInt32(bm.Groups["len"].Captures[0].Value);
                    string reason = bm.Groups["reason"].Captures[0].Value;
                    string adder = bm.Groups["adder"].Captures[0].Value;

                    //Similar to ListManager.handleListCommand
                    switch (action)
                    {
                        case "ADD":
                            switch (list)
                            {
                                case "WL":
                                    listman.addUserToList(item, "", ListManager.UserType.whitelisted, adder, reason, len, ref listman.dbcon);
                                    break;
                                case "BL":
                                    listman.addUserToList(item, "", ListManager.UserType.blacklisted, adder, reason, len, ref listman.dbcon);
                                    break;
                                case "GL":
                                    listman.addUserToList(item, "", ListManager.UserType.greylisted, adder, reason, len, ref listman.dbcon);
                                    break;
                                case "BNU":
                                    listman.addItemToList(item, 11, adder, reason, len);
                                    break;
                                case "BNA":
                                    listman.addItemToList(item, 12, adder, reason, len);
                                    break;
                                case "BES":
                                    listman.addItemToList(item, 20, adder, reason, len);
                                    break;
                                case "CVP":
                                    listman.addPageToWatchlist(item, "", adder, reason, len);
                                    break;
                                //Gracefully ignore unknown message types
                            }
                            break;
                        case "DEL":
                            switch (list)
                            {
                                case "WL":
                                    listman.delUserFromList(item, "", ListManager.UserType.whitelisted);
                                    break;
                                case "BL":
                                    listman.delUserFromList(item, "", ListManager.UserType.blacklisted);
                                    break;
                                case "GL":
                                    listman.delUserFromList(item, "", ListManager.UserType.greylisted);
                                    break;
                                case "BNU":
                                    listman.delItemFromList(item, 11);
                                    break;
                                case "BNA":
                                    listman.delItemFromList(item, 12);
                                    break;
                                case "BES":
                                    listman.delItemFromList(item, 20);
                                    break;
                                case "CVP":
                                    listman.delPageFromWatchlist(item, "");
                                    break;
                                //Gracefully ignore unknown message types
                            }
                            break;
                        case "FIND":
                            if (list == "BLEEP")
                                if (prjlist.ContainsKey(item))
                                    SendMessageF(SendType.Action, reason, "has " + item + ", " + adder + " :D", false, true);
                            break;
                        case "COUNT":
                            if (list == "BLEEP")
                                SendMessageF(SendType.Action, reason, "owns " + prjlist.Count.ToString() + " wikis; version is " + version,
                                    false, true);
                            break;
                        case "CONFIG":
                            if (list == "BLEEP")
		                        BotConfigMsg(reason);
                            break;
                        
                        //Gracefully ignore unknown action types
                    }
                }
                catch (Exception ex)
                {
                    BroadcastDD("ERROR", "BC_ERROR", ex.Message, e.Data.Message);
                }
            }
        }

        static void irc_OnConnected(object sender, EventArgs e)
        {
            logger.Info("Connected to " + ircServerName);
        }


        #region Flood protection code

        /// <summary>
        /// Route all irc.SendMessage() calls through this to use the queue
        /// </summary>
        public static void SendMessageF(SendType type, string destination, string message, bool IsDroppable, bool IsPriority)
        {
            QueuedMessage qm = new QueuedMessage();
            qm.type = type;
            qm.message = message;
            qm.destination = destination;
            qm.SentTime = DateTime.Now.Ticks;
            qm.IsDroppable = IsDroppable;

            if (IsPriority)
                lock (priQueue)
                    priQueue.Enqueue(qm);
            else
                lock (fcQueue)
                    fcQueue.Enqueue(qm);

            //logger.Info("Queued item");
        }

        static void irc_OnPong(object sender, PongEventArgs e)
        {
            sentLength = 0;
            dontSendNow = false;
            sendlock.Set();
            //logger.Info("Got pong: " + e.Data.RawMessage);
            irc.LastPongReceived = DateTime.Now; //Hacked SmartIrc4net
        }

        /// <summary>
        /// Calculates the rough length, in bytes, of a queued message
        /// </summary>
        /// <param name="qm"></param>
        /// <returns></returns>
        static int calculateByteLength(QueuedMessage qm)
        {
            // PRIVMSG #channelname :My message here (10 + destination + message)
            // NOTICE #channelname :My message here (9 + dest + msg)
            if (qm.type == SendType.Notice)
                return 11 + System.Text.ASCIIEncoding.Unicode.GetByteCount(qm.message)
                    + System.Text.ASCIIEncoding.Unicode.GetByteCount(qm.destination);
            else
                return 12 + System.Text.ASCIIEncoding.Unicode.GetByteCount(qm.message)
                    + System.Text.ASCIIEncoding.Unicode.GetByteCount(qm.destination);
        }

        /// <summary>
        /// Thread function that runs continuously in the background, sending messages
        /// </summary>
        static void msgthread()
        {
            Thread.CurrentThread.Name = "Messaging";
            Thread.CurrentThread.IsBackground = true; //Allow runtime to close this thread at shutdown
            //Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            logger.Info("Started messaging");

            while (irc.IsConnected) {
                QueuedMessage qm;

                //Console.WriteLine("Lag is " + irc.Lag.ToString());

                // First check for any priority messages to send
                if (priQueue.Count > 0)
                {
                    // We have priority messages to handle
                    lock (priQueue)
                        qm = (QueuedMessage)priQueue.Dequeue();
                }
                else
                {
                    // No priority messages; let's handle the regular-class messages
                    // Do we have any messages to handle?
                    if (fcQueue.Count == 0)
                    {
                        // No messages at all to handle
                        Thread.Sleep(50); // Sleep for 50 miliseconds
                        continue; // Start the loop over
                    }

                    // We do have a message to dequeue, so dequeue it
                    lock (fcQueue)
                        qm = (QueuedMessage)fcQueue.Dequeue();

                    //logger.Info(fcQueue.Count.ToString() + " in normal. sentLength: " + sentLength.ToString());
                }


                // Okay, we now have a message to handle in qm

                // Is our message too old?
                if (qm.IsDroppable && (DateTime.Now.Ticks - qm.SentTime > maxlag))
                {
                    //logger.Info("Lost packet");
                    continue; // Start the loop over
                }

                // If it's okay to send now, but we would exceed the bufflen if we were to send it
                if (!dontSendNow && (sentLength + calculateByteLength(qm) + 2 > bufflen))
                {
                    // Ping the server and wait for a reply
                    irc.RfcPing(ircServerName); //Removed Priority.Critical
                    irc.LastPingSent = DateTime.Now; //Hacked SmartIrc4net
                    sendlock.Reset();
                    dontSendNow = true;
                    //logger.Info("Waiting for artificial PONG");
                }

                // Sleep while it's not okay to send
                while (dontSendNow)
                    Thread.Sleep(1000);
                //sendlock.WaitOne();

                // Okay, we can carry on now. Is our message still fresh?
                if (qm.IsDroppable && (DateTime.Now.Ticks - qm.SentTime > maxlag))
                // Oops, sowwy. Our message has rotten.
                {
                    //logger.Info("Lost packet");
                    continue; // Start the loop over
                }

                // At last! Send the damn thing!
                // ...but only if we're still connected
                if (irc.IsConnected)
                {
                    sentLength = sentLength + calculateByteLength(qm) + 2;
                    irc.SendMessage(qm.type, qm.destination, qm.message);
                }

                //logger.Info("Lag was " + (DateTime.Now.Ticks - qm.SentTime));

                // Throttle on our part
                Thread.Sleep(300);
            }

            logger.Info("Thread ended");
        }

        #endregion

        static bool hasPrivileges(char minimum, ref IrcEventArgs e)
        {
            switch (minimum)
            {
                case '@':
                    if (!irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsOp)
                    {
                        SendMessageF(SendType.Notice, e.Data.Nick, (String)msgs["00122"], false, true);
                        return false;
                    }
                    else
                        return true;
                case '+':
                    if (!irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsOp && !irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsVoice)
                    {
                        SendMessageF(SendType.Notice, e.Data.Nick, (String)msgs["00120"], false, true);
                        return false;
                    }
                    else
                        return true;
                default:
                    return false;
            }
        }

        static void irc_OnChannelMessage(object sender, IrcEventArgs e)
        {
            if (e.Data.Message == "" || e.Data.Message == null)
                return; //Prevent empty messages from crashing the bot

            Match cmdMatch = botCmd.Match(e.Data.Message);

            if (cmdMatch.Success)
            {
                // Have to be voiced to issue any commands
                if (!hasPrivileges('+', ref e))
                    return;

                string command = cmdMatch.Groups["command"].Captures[0].Value;

                string extraParams;
                try
                {
                    extraParams = cmdMatch.Groups["params"].Captures[0].Value.Trim();
                }
                catch (Exception)
                {
                    extraParams = "";
                }

                string[] cmdParams = extraParams.Split(new char[1] { ' ' });

                switch (command)
                {
                    case "quit":
                        if (!hasPrivileges('@', ref e))
                            return;
                        logger.Info(e.Data.Nick + " ordered a quit");
                        PartIRC((string)mainConfig["partmsg"]);
                        Exit();
                        break;
                    case "restart":
                        if (!hasPrivileges('@', ref e))
                            return;
                        logger.Info(e.Data.Nick + " ordered a restart");
                        PartIRC("Rebooting by order of " + e.Data.Nick + " ...");
                        Restart();
                        break;
                    case "status":
                        TimeSpan ago = DateTime.Now.Subtract(rcirc.lastMessage);
                        SendMessageF(SendType.Message, e.Data.Channel, "Last message was received on RCReader "
                            + ago.TotalSeconds + " seconds ago", false, false);
                        break;
                    case "help":
                        SendMessageF(SendType.Message, e.Data.Channel, (String)msgs["20005"], false, true);
                        break;
                    case "version":
                    case "settings":
                    case "config":
                        BotConfigMsg(e.Data.Channel);
                        if (cmdParams[0] == "all")
                        {
                            Broadcast("BLEEP", "CONFIG", "BLEEP", 0, e.Data.Channel, e.Data.Nick);
                        }
                        break;
                    case "msgs":
                        //Reloads msgs
                        if (!hasPrivileges('@', ref e))
                            return;
                        readMessages((string)mainConfig["messages"]);
                        SendMessageF(SendType.Message, e.Data.Channel, "Re-read messages", false, false);
                        break;
                    case "reload":
                        //Reloads wiki data for a project
                        if (!hasPrivileges('@', ref e))
                            return;
                        try
                        {
                            if (!prjlist.ContainsKey(cmdParams[0]))
                                throw new Exception("Project " + cmdParams[0] + " is not loaded");

                            ((Project)prjlist[cmdParams[0]]).retrieveWikiDetails();
                            SendMessageF(SendType.Message, e.Data.Channel, "Reloaded project " + cmdParams[0], false, false);
                        }
                        catch (Exception ex)
                        {
                            SendMessageF(SendType.Message, e.Data.Channel, "Unable to reload: " + ex.Message, false, true);
                            logger.Error("Reload project failed: " + ex.Message);
                        }
                        break;
                    case "load":
                        if (!hasPrivileges('@', ref e))
                            return;
                        try
                        {
                            if (cmdParams.Length == 2)
                                prjlist.addNewProject(cmdParams[0], cmdParams[1]);
                            else
                                prjlist.addNewProject(cmdParams[0], "");

                            SendMessageF(SendType.Message, e.Data.Channel, "Loaded new project " + cmdParams[0], false, true);
                            //Automatically get admins and bots:
                            Thread.Sleep(200);
                            SendMessageF(SendType.Message, e.Data.Channel, listman.configGetAdmins(cmdParams[0]), false, false);
                            Thread.Sleep(500);
                            SendMessageF(SendType.Message, e.Data.Channel, listman.configGetBots(cmdParams[0]), false, false);
                        }
                        catch (Exception ex)
                        {
                            SendMessageF(SendType.Message, e.Data.Channel, "Unable to add project: " + ex.Message, false, true);
                            logger.Error("Add project failed: " + ex.Message);
                        }
                        break;
                    case "bleep":
                        if (!hasPrivileges('+', ref e))
                            return;
                        try
                        {
                            if (cmdParams[0].Length > 0)
                            {
                                if (prjlist.ContainsKey(cmdParams[0]))
                                {
                                    SendMessageF(SendType.Action, e.Data.Channel, "has " + cmdParams[0] + ", " + e.Data.Nick + " :D", false, true);
                                }
                                else
                                {
                                    Broadcast("BLEEP", "FIND", cmdParams[0], 0, e.Data.Channel, e.Data.Nick);
                                    SendMessageF(SendType.Message, e.Data.Channel, "Bleeped. Please wait for a reply.", false, true);
                                }
                            }
                        } catch (Exception ex)
                        {
                            SendMessageF(SendType.Message, e.Data.Channel, "Unable to bleep: " + ex.Message, false, true);
                        }
                        break;
                    case "count":
                        if (!hasPrivileges('+', ref e))
                            return;
                        Broadcast("BLEEP", "COUNT", "BLEEP", 0, e.Data.Channel, e.Data.Nick);
                        SendMessageF(SendType.Action, e.Data.Channel, "owns " + prjlist.Count.ToString() + " wikis; version is " + version,
                            false, true);
                        break;
                    case "drop":
                        if (!hasPrivileges('@', ref e))
                            return;
                        try
                        {
                            prjlist.deleteProject(cmdParams[0]);
                            SendMessageF(SendType.Message, e.Data.Channel, "Deleted project " + cmdParams[0], false, true);
                        }
                        catch (Exception ex)
                        {
                            SendMessageF(SendType.Message, e.Data.Channel, "Unable to delete project: " + ex.Message, false, true);
                            logger.Error("Delete project failed: " + ex.Message);
                        }
                        break;
                    case "list":
                        string result = "Currently monitoring: ";
                        foreach (string p in prjlist.Keys)
                        {
                            result += p + " ";
                        }
                        result += "(Total: " + prjlist.Count.ToString() + " wikis)";
                        foreach (string chunk in SWMTUtils.stringSplit(result, 400))
                            SendMessageF(SendType.Message, e.Data.Channel, chunk, false, true);
                        break;
                    case "batchgetusers":
                        if (!hasPrivileges('@', ref e))
                            return;
                        listman.currentGetBatchChannel = e.Data.Channel;
                        new Thread(new ThreadStart(listman.BatchGetAllAdminsAndBots)).Start();
                        break;
                    case "bl":
                        SendMessageF(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(1, e.Data.Nick, extraParams), false, true);
                        break;
                    case "wl":
                        SendMessageF(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(0, e.Data.Nick, extraParams), false, true);
                        break;
                    case "gl":
                        SendMessageF(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(6, e.Data.Nick, extraParams), false, true);
                        break;
                    case "al":
                        SendMessageF(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(2, e.Data.Nick, extraParams), false, true);
                        break;
                    case "bots":
                    case "bot":
                        SendMessageF(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(5, e.Data.Nick, extraParams), false, true);
                        break;
                    case "cvp":
                        SendMessageF(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(10, e.Data.Nick, extraParams), false, true);
                        break;
                    case "bnu":
                        SendMessageF(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(11, e.Data.Nick, extraParams), false, true);
                        break;
                    case "bna":
                        SendMessageF(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(12, e.Data.Nick, extraParams), false, true);
                        break;
                    case "bes":
                        SendMessageF(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(20, e.Data.Nick, extraParams), false, true);
                        break;

                    //_1568: Restrict the "get" command to ops
                    case "getadmins":
                        if (!hasPrivileges('@', ref e))
                            return;
                            SendMessageF(SendType.Message, e.Data.Channel, listman.configGetAdmins(extraParams), false, true);
                            break;
                     case "getbots":
                        if (!hasPrivileges('@', ref e))
                            return;
                            SendMessageF(SendType.Message, e.Data.Channel, listman.configGetBots(extraParams), false, true);
                            break;

                    case "intel":
                        string intelResult = listman.GlobalIntel(extraParams);
                        foreach (string chunk in intelResult.Split(new char[1] {'\n'}))
                        {
                            SendMessageF(SendType.Message, e.Data.Channel, chunk, false, true);
                            Thread.Sleep(400);
                        }
                        break;
                    case "purge":
                        if (!hasPrivileges('@', ref e))
                            return;
                        SendMessageF(SendType.Message, e.Data.Channel, listman.purgeWikiData(extraParams), false, true);
                        break;
                    case "batchreload":
                        if (!hasPrivileges('@', ref e))
                            return;
                        prjlist.currentBatchReloadChannel = e.Data.Channel;
                        new Thread(new ThreadStart(prjlist.reloadAllWikis)).Start();
                        break;
                }
            }
        }

        /// <summary>
        /// Reads messages from filename (Console.msgs) into SortedList msgs
        /// </summary>
        /// <param name="filename">File to read messages from</param>
        static void readMessages(string filename)
        {
            msgs.Clear();
            try
            {
                using (StreamReader sr = new StreamReader(filename))
                {
                    String line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.StartsWith("#") || (line == ""))
                        {
                            //Ignore: comment or blank line
                        }
                        else
                        {
                            string[] parts = line.Split(new char[1] { '=' }, 2);
                            msgs.Add(parts[0], parts[1].Replace(@"%c", "\x03").Replace(@"%b", "\x02"));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error("Unable to read messages from file", e);
            }
        }

        /// <summary>
        /// Gets a message from the msgs store
        /// </summary>
        /// <param name="msgCode">The five-digit message code</param>
        /// <param name="attributes">The attributes to place in the message</param>
        /// <returns></returns>
        static string getMessage(int msgCode, ref Hashtable attributes)
        {
            try
            {
                string message = (string)msgs[msgCode.ToString().PadLeft(5,'0')];
                foreach (DictionaryEntry de in attributes)
                {
                    message = message.Replace("${" + (string)de.Key + "}", (string)de.Value);
                }
                return message;
            }
            catch (Exception e)
            {
                logger.Error("Cannot getMessage", e);
                return "[Error: cannot get message]";
            }
        }

        /// <summary>
        /// Get a message from the msgs store, and format it using the parameters specified.
        /// Messages should be those with a "1" prefix, incompatible with CVUBot.
        /// </summary>
        /// <param name="msgCode">The five-digit message code</param>
        /// <param name="fParams">The parameters to place in the string</param>
        /// <returns></returns>
        public static string getFormatMessage(int msgCode, params String[] fParams) {
            try
            {
                string message = (string)msgs[msgCode.ToString().PadLeft(5, '0')];
                return String.Format(message, fParams);
            }
            catch (Exception e)
            {
                logger.Error("Cannot getFormatMessage " + msgCode.ToString(), e);
                return "[Error: cannot get message]";
            }
        }

        public static void Broadcast(string list, string action, string item, int expiry, string reason, string adder)
        {
            if (BroadcastChannel == "None")
                return;
            string bMsg = "*%BB/1.1%B*" + list + "*" + action + "*%C07%B" + item + "%B%C*%C13" + expiry.ToString()
                + "%C*%C09%B" + reason + "%B%C*%C11%B" + adder + "%C%B*";
            SendMessageF(SendType.Notice, BroadcastChannel, bMsg.Replace(@"%C", "\x03").Replace(@"%B", "\x02"), false, true);
        }

        public static void BroadcastDD(string type, string codename, string message, string ingredients)
        {
            if (BroadcastChannel == "None")
                return;
            string bMsg = "*%BDD/1.0%B*" + type + "*" + codename + "*%C07%B" + message + "%B%C*%C13" + ingredients + "%C*";
            SendMessageF(SendType.Notice, BroadcastChannel, bMsg.Replace(@"%C", "\x03").Replace(@"%B", "\x02"), false, true);
            logger.Info("Broadcasted DD: " + type + "," + codename + "," + message + "," + ingredients);
        }

        /// <summary>
        /// Shorthand greylisting function for use by ReactToRCEvent
        /// </summary>
        /// <param name="userOffset"></param>
        /// <param name="username"></param>
        /// <param name="reason"></param>
        private static void AddToGreylist(int userOffset, string username, string reason)
        {
            //Only if blacklisted, anon, user, or already greylisted
            if ((userOffset == 1) || (userOffset == 4) || (userOffset == 3) || (userOffset == 6))
            {
                IDbConnection rcdbcon = (IDbConnection)new SqliteConnection(listman.connectionString);
                rcdbcon.Open();
                listman.addUserToList(username, "", ListManager.UserType.greylisted, "SWMTBot", reason, 1, ref rcdbcon);
                rcdbcon.Close();
                rcdbcon = null;
                Broadcast("GL", "ADD", username, 900, reason, "SWMTBot"); //Greylist for 900 seconds = 15 mins * 60 secs
            }
        }

        /// <summary>
        /// Reacts to the RC Event, passed from RCReader. Remember: this runs in the RCReader thread!
        /// </summary>
        /// <param name="r"></param>
        public static void ReactToRCEvent(RCEvent r)
        {
            int feedFilterThis = 1;
            
            // Feed filters -> Event
            // Peform these checks before even classifying the user
            // EventType is available right away, thus saving a db connection when settings are on ignore
                if(r.eventtype == RCEvent.EventType.upload)
                    feedFilterThis = feedFilterEventUpload;
                
                if (IsCubbie && (r.eventtype != RCEvent.EventType.upload))
                    return;//If this IsCubbie, then ignore non-uploads
                
                if(feedFilterThis == 4)// 4 is "ignore"
                    return;
            
            Hashtable attribs = new Hashtable();
            String message = "";
            int userOffset = (int)(listman.classifyEditor(r.user, r.project));

            /* If this is a bot action, and if bot edits are ignored, return */
            /* HACK: If this is a bot admin (not currently supported), and it blocks, then the user will not be blacklisted */
            if (ignoreBotEdits && (userOffset == 5))
                return;

            // Feed filters -> Users
                if(userOffset == 3)
                    feedFilterThis = feedFilterUsersAnon;
                
                if(userOffset == 4)
                    feedFilterThis = feedFilterUsersReg;
                
                if(feedFilterThis == 4)// 4 is "ignore"
                    return;

            switch (r.eventtype)
            {
                case RCEvent.EventType.edit:
                    //This case handles: New pages, Edited pages
                    String diffsize;
                    if (r.szdiff >= 0)
                        diffsize = "+" + r.szdiff.ToString();
                    else
                        diffsize = r.szdiff.ToString();
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("article", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("carticle", r.title);
                    attribs.Add("size", diffsize);
                    attribs.Add("url", r.url);
                    attribs.Add("reason", r.comment);

                    if (r.newpage)
                    {
                        bool createNothingSpecial = false;

                        // First, just check sizes, and assign default messages in case nothing else is at fault
                        if (r.szdiff >= newbig)
                        {
                            attribs.Add("sizeattrib", getMessage(100, ref attribs));
                            attribs.Add("sizereset", getMessage(102, ref attribs));
                            message = getMessage(5010 + userOffset, ref attribs);
                        }
                        else if (r.szdiff <= newsmall)
                        {
                            attribs.Add("sizeattrib", getMessage(101, ref attribs));
                            attribs.Add("sizereset", getMessage(103, ref attribs));
                            message = getMessage(5020 + userOffset, ref attribs);
                        }
                        else
                        {
                            attribs.Add("sizeattrib", "");
                            attribs.Add("sizereset", "");
                            message = getMessage(5000 + userOffset, ref attribs);
                            createNothingSpecial = true;
                        }

                        //If this is a blacklisted or greylisted user, create is always special
                        if ((userOffset == 1) || (userOffset == 6))
                            createNothingSpecial = false;

                        //If this is an anon and anons should be shown, create is always special
                        if ((userOffset == 3) && (feedFilterUsersAnon == 1))
                            createNothingSpecial = false;

                        //If this is an normal user and normal users should be shown, create is always special
                        if ((userOffset == 4) && (feedFilterUsersReg == 1))
                            createNothingSpecial = false;

                        // Now check if the edit summary matches BES
                        listMatch lm = listman.matchesList(r.comment, 20);
                        if (lm.Success)
                        {
                            //Matches BES
                            attribs.Add("watchword", lm.matchedItem);
                            //attribs.Add("reason", lm.matchedReason);
                            message = getMessage(95040 + userOffset, ref attribs);
                            createNothingSpecial = false;
                            AddToGreylist(userOffset, r.user, Program.getFormatMessage(16300, (String)attribs["article"], lm.matchedItem));
                        }

                        // Now check if this page title matches BNA
                        listMatch eslm = listman.matchesList(r.title, 12);
                        if (eslm.Success)
                        {
                            //Matches BNA
                            attribs.Add("watchword", eslm.matchedItem);
                            //attribs.Add("reason", eslm.matchedReason);
                            message = getMessage(5040 + userOffset, ref attribs);
                            createNothingSpecial = false;
                            AddToGreylist(userOffset, r.user, Program.getFormatMessage(16300, (String)attribs["article"], eslm.matchedItem));
                        }

                        // Now check if user has created a watched page
                        listMatch wlm = listman.isWatchedArticle(r.title, r.project);
                        if (wlm.Success)
                        {
                            //Is watched
                            //attribs.Add("reason", wlm.matchedReason);
                            message = getMessage(5030 + userOffset, ref attribs);
                            createNothingSpecial = false;
                            AddToGreylist(userOffset, r.user, Program.getFormatMessage(16301, (String)attribs["article"]));
                        }

                        // If created by an admin, bot or whitelisted person
                        if ((userOffset == 2) || (userOffset == 5) || (userOffset == 0))
                            return;

                        // If created by a user and nothing special
                        if ((userOffset == 4) && (createNothingSpecial))
                            return;

                        // If else: user is on blacklist, or, user is on greylisted, or feedFilter setting made it special
                            // show, continue, dont return! 
                    }
                    else
                    { //Not new page; a simple edit
                        bool editNothingSpecial = false;

                        if (r.szdiff >= editbig)
                        {
                            attribs.Add("sizeattrib", getMessage(100, ref attribs));
                            attribs.Add("sizereset", getMessage(102, ref attribs));
                            message = getMessage(5110 + userOffset, ref attribs);
                        }
                        else if (r.szdiff <= editblank)
                        {
                            attribs.Add("sizeattrib", getMessage(101, ref attribs));
                            attribs.Add("sizereset", getMessage(103, ref attribs));
                            message = getMessage(5120 + userOffset, ref attribs);
                        }
                        else
                        {
                            attribs.Add("sizeattrib", "");
                            attribs.Add("sizereset", "");
                            message = getMessage(5100 + userOffset, ref attribs);
                            editNothingSpecial = true;
                        }

                        //If this is a blacklisted or anon or greylisted user, edit is always special
                        if ((userOffset == 1) || (userOffset == 6))
                            editNothingSpecial = false;

                        //If this is an anon and anons should be shown, edit is always special
                        if ((userOffset == 3) && (feedFilterUsersAnon == 1))
                            editNothingSpecial = false;

                        //If this is an normal user and normal users should be shown, edit is always special
                        if ((userOffset == 4) && (feedFilterUsersReg == 1))
                            editNothingSpecial = false;

                        // Now check if user has edited a watched page
                        listMatch welm = listman.isWatchedArticle(r.title, r.project);
                        if (welm.Success)
                        {
                            //Is watched
                            //attribs.Add("reason", welm.matchedReason); //Current Console.msgs provides reason field for edsum only
                            message = getMessage(5130 + userOffset, ref attribs);
                            editNothingSpecial = false;
                        }

                        // Now check if user has actually blanked the page
                        if (((Project)prjlist[r.project]).rautosummBlank.IsMatch(r.comment))
                        {
                            message = getMessage(96010 + userOffset, ref attribs);
                            editNothingSpecial = false;
                            AddToGreylist(userOffset, r.user, Program.getFormatMessage(16311, (String)attribs["article"]));
                        }
                        else //i.e., it won't be both a blank and a replace, we want to save some resources
                        {
                            Match rplm = ((Project)prjlist[r.project]).rautosummReplace.Match(r.comment);
                            if (rplm.Success)
                            {
                                //It's a replace :(
                                try
                                {
                                    attribs.Add("profanity", rplm.Groups["item1"].Captures[0].Value);
                                    message = getMessage(96020 + userOffset, ref attribs);
                                }
                                catch (ArgumentOutOfRangeException)
                                {
                                    //This wiki probably doesn't have a profanity attribute
                                    message = getMessage(96030 + userOffset, ref attribs);
                                }
                                editNothingSpecial = false;
                            }
                        }

                        // Now check if the edit summary matches BES
                        listMatch elm = listman.matchesList(r.comment, 20);
                        if (elm.Success)
                        {
                            //Matches BES
                            attribs.Add("watchword", elm.matchedItem);
                            //attribs.Add("reason", elm.matchedReason);
                            message = getMessage(95130 + userOffset, ref attribs);
                            editNothingSpecial = false;
                            AddToGreylist(userOffset, r.user, Program.getFormatMessage(16310, r.comment, (String)attribs["article"]));
                        }

                        // If nothing special about the edit (i.e., it's normal-sized, it's not on a watched page), return
                        if (editNothingSpecial)
                            return;
                    }
                    break;
                case RCEvent.EventType.move:
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("fromname", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("cfromname", r.title);
                    attribs.Add("toname", ((Project)prjlist[r.project]).interwikiLink + r.movedTo);
                    attribs.Add("ctoname", r.movedTo);
                    attribs.Add("url", r.blockLength); //The blockLength field stores the moveFrom URL
                    attribs.Add("reason", r.comment);
                    message = getMessage(5500 + userOffset, ref attribs);
                    break;
                case RCEvent.EventType.block:
                    attribs.Add("blockname", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("cblockname", r.title.Split(new char[1] { ':' }, 2)[1]);
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("talkurl", ((Project)prjlist[r.project]).rooturl + "wiki/User_talk:" + SWMTUtils.wikiEncode(r.user));
                    attribs.Add("length", r.blockLength);
                    attribs.Add("reason", r.comment);
                    message = getMessage(5400, ref attribs);
                    //If the blocked user (r.title) isn't botlisted, add to blacklist
                    if (listman.classifyEditor(r.title.Split(new char[1] { ':' }, 2)[1], r.project) != ListManager.UserType.bot)
                    {
                        //If this isn't an indefinite/infinite block, add to blacklist
                        //Since we're in the RCReader thread, and we'll be writing to the db, we better open a new connection
                        IDbConnection rcdbcon = (IDbConnection)new SqliteConnection(listman.connectionString);
                        rcdbcon.Open();
                        if ((r.blockLength.ToLower() != "indefinite") && (r.blockLength.ToLower() != "infinite"))
                        {                                                               //345,600 seconds = 96 hours
                            int listLen = Convert.ToInt32(SWMTUtils.ParseDateTimeLength(r.blockLength, 345600) * 2.5);
                            string blComment = "Autoblacklist: " + r.comment + " on " + r.project;
                            message += "\n" + listman.addUserToList(r.title.Split(new char[1] { ':' }, 2)[1], "" //Global bl
                                , ListManager.UserType.blacklisted, r.user, blComment , listLen, ref rcdbcon);
                            Broadcast("BL", "ADD", r.title.Split(new char[1] { ':' }, 2)[1], listLen, blComment, r.user);
                        }
                        rcdbcon.Close();
                        rcdbcon = null;
                    }
                    break;
                case RCEvent.EventType.unblock:
                    attribs.Add("blockname", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("cblockname", r.title.Split(new char[1] { ':' }, 2)[1]);
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("talkurl", ((Project)prjlist[r.project]).rooturl + "wiki/User_talk:" + SWMTUtils.wikiEncode(r.user));
                    attribs.Add("reason", r.comment);
                    message = getMessage(5700, ref attribs);
                    break;
                case RCEvent.EventType.delete:
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("article", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("carticle", r.title);
                    attribs.Add("url", ((Project)prjlist[r.project]).rooturl + "wiki/" + SWMTUtils.wikiEncode(r.title));
                    attribs.Add("reason", r.comment);
                    message = getMessage(05300, ref attribs);
                    break;
                case RCEvent.EventType.newuser:
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("blockurl", ((Project)prjlist[r.project]).rooturl + "wiki/Special:Blockip/" + SWMTUtils.wikiEncode(r.user));
                    attribs.Add("talkurl", ((Project)prjlist[r.project]).rooturl + "wiki/User_talk:" + SWMTUtils.wikiEncode(r.user));
                    listMatch bnuMatch = listman.matchesList(r.user, 11);
                    if (bnuMatch.Success)
                    {
                        // Matches BNU
                        attribs.Add("watchword", bnuMatch.matchedItem);
                        attribs.Add("wwreason", bnuMatch.matchedReason);
                        message = getMessage(5201, ref attribs);
                        AddToGreylist(userOffset, r.user, Program.getFormatMessage(16320, bnuMatch.matchedItem));
                    }
                    else
                        message = getMessage(5200, ref attribs);
                    break;
                case RCEvent.EventType.newuser2:
                    attribs.Add("creator", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("ccreator", r.user);
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.title);
                    attribs.Add("ceditor", r.title);
                    attribs.Add("blockurl", ((Project)prjlist[r.project]).rooturl + "wiki/Special:Blockip/" + SWMTUtils.wikiEncode(r.user));
                    attribs.Add("talkurl", ((Project)prjlist[r.project]).rooturl + "wiki/User_talk:" + SWMTUtils.wikiEncode(r.user));
                    listMatch bnuMatch2 = listman.matchesList(r.user, 11);
                    if (bnuMatch2.Success)
                    {
                        // Matches BNU
                        attribs.Add("watchword", bnuMatch2.matchedItem);
                        attribs.Add("wwreason", bnuMatch2.matchedReason);
                        message = getMessage(5211, ref attribs);
                        AddToGreylist(userOffset, r.user, Program.getFormatMessage(16320, bnuMatch2.matchedItem));
                    }
                    else
                        message = getMessage(5210, ref attribs);
                    break;
                case RCEvent.EventType.upload:
                    int uMsg = 5600;

                    // Check if the edit summary matches BES
                    listMatch ubes2 = listman.matchesList(r.comment, 20);
                    if (ubes2.Success)
                    {
                        attribs.Add("watchword", ubes2.matchedItem);
                        attribs.Add("lmreason", ubes2.matchedReason);
                        uMsg = 95620;
                    }

                    // Now check if the title matches BES
                    listMatch ubes1 = listman.matchesList(r.title, 20);
                    if (ubes1.Success)
                    {
                        attribs.Add("watchword", ubes1.matchedItem);
                        attribs.Add("lmreason", ubes1.matchedReason);
                        uMsg = 95620;
                    }

                    // Check if upload is watched
                    listMatch uwa = listman.isWatchedArticle(r.title, r.project);
                    if (uwa.Success)
                        uMsg = 5610;

                    // If normal and uploaded by an admin, bot or whitelisted person (TODO: unless watched or matches word)
                    if ((uMsg == 5600) && ((userOffset == 2) || (userOffset == 5) || (userOffset == 0)))
                        return;
                        
                    // if normal and uploads are softhidden and user is normal user or anon
                    if ((uMsg == 5600) && (feedFilterEventUpload == 2) && ((userOffset == 3) || (userOffset == 4)))
                        return;
                    
                    // If our message is 95620, we might need to truncate r.comment
                    if (uMsg == 95620)
                    {
                        if (r.comment.Length > 25)
                            r.comment = r.comment.Substring(0, 23) + "...";
                    }

                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("uploaditem", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("cuploaditem", r.title);
                    attribs.Add("reason", r.comment);
                    attribs.Add("url", ((Project)prjlist[r.project]).rooturl + "wiki/" + SWMTUtils.wikiEncode(r.title));
                    message = getMessage(userOffset + uMsg, ref attribs);
                    break;
                case RCEvent.EventType.protect:
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("article", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("carticle", r.title);
                    attribs.Add("comment", r.comment);
                    //'url' in protect is broken, it also contains " [move=sysop] (indefinite)" etc.
                    //attribs.Add("url", ((Project)prjlist[r.project]).rooturl + "wiki/" + SWMTUtils.wikiEncode(r.title));
                    message = getMessage(5900, ref attribs);
                    break;
                case RCEvent.EventType.unprotect:
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("article", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("carticle", r.title);
                    attribs.Add("comment", r.comment);
                    //'url' in unprotect is fine, it's just the pagetitle
                    attribs.Add("url", ((Project)prjlist[r.project]).rooturl + "wiki/" + SWMTUtils.wikiEncode(r.title));
                    message = getMessage(5901, ref attribs);
                    break;
                case RCEvent.EventType.modifyprotect:
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("article", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("carticle", r.title);
                    attribs.Add("comment", r.comment);
                    //'url' in modifyprotect is broken, it also contains " [move=sysop] (indefinite)" etc.
                    //attribs.Add("url", ((Project)prjlist[r.project]).rooturl + "wiki/" + SWMTUtils.wikiEncode(r.title));
                    message = getMessage(5902, ref attribs);
                    break;
            }
            
            if (feedFilterThis == 3)
            {
                //autolistings have been done throughout ReactToRCEvent()
                //if hardhide, just dont send the message now
                message = "";
            }

            if (message != "")
            {
                //Allow multiline
                foreach (string line in message.Split(new char[1] { '\n' }))
                {
                    //Chunk messages that are too long
                    foreach (string chunk in SWMTUtils.stringSplit(line, 400))
                    {
                        if ((chunk.Trim() != "\"\"") && (chunk.Trim() != "\""))
                            SendMessageF(SendType.Message, FeedChannel, chunk, true, false);
                    }
                }
            }
        }
        
        public static void BotConfigMsg(string destChannel)
        {
        
	        string settingsmessage = "runs version: " + version + " in " + FeedChannel + "; settings: editblank:" + editblank + ", editbig:" + editbig + ", newbig:" + newbig + ", newsmall:" + newsmall + ", feedFilterUsersAnon:" + feedFilterUsersAnon + ", feedFilterUsersReg:" + feedFilterUsersReg + ", feedFilterEventUpload:" + feedFilterEventUpload;
	        settingsmessage += IsCubbie ? ", IsCubbie:true" : ", IsCubbie:false";
	        settingsmessage += disableClassifyEditor ? ", disableClassifyEditor:true" : ", disableClassifyEditor:false";
	
	        foreach (string chunk in SWMTUtils.stringSplit(settingsmessage, 400))
	            SendMessageF(SendType.Action, destChannel, chunk, false, true);
        
        }

        public static void Exit()
        {
            try
            {
                //Delayed quitting after parting in PartIRC()
                irc.Disconnect();
                rcirc.rcirc.AutoReconnect = false;
                rcirc.rcirc.Disconnect();

                listman.closeDBConnection();
                LogManager.Shutdown();
            }
            catch
            {
                //Ignore
            }
            finally
            {
                System.Environment.Exit(0);
            }
        }


        public static void Restart()
        {
            //If a custom restartcmd / restartarg has been set in the main config, use that
            if (mainConfig.ContainsKey("restartcmd"))
            {
                //Execute the custom command
                System.Diagnostics.Process.Start((string)mainConfig["restartcmd"], (string)mainConfig["restartarg"]);
            }
            else
            {
                //Note: argument is not actually used, but it's there to prevent a mono bug
                System.Diagnostics.Process.Start(System.Reflection.Assembly.GetExecutingAssembly().Location, "--restart");
            }
            Exit();
        }

        public static void PartIRC(string quitMessage)
        {
            rcirc.rcirc.AutoReconnect = false;
            rcirc.rcirc.RfcQuit(quitMessage);
            irc.RfcPart(ControlChannel, quitMessage);
            irc.RfcPart(FeedChannel, quitMessage);
            irc.RfcPart(BroadcastChannel, quitMessage);
            Thread.Sleep(1000);
        }
    }
}