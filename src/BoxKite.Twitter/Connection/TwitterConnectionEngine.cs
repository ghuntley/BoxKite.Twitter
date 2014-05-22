﻿// (c) 2012-2013 Nick Hodge mailto:hodgenick@gmail.com & Brendan Forster
// License: MS-PL

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using BoxKite.Twitter.Models;

namespace BoxKite.Twitter
{
    public partial class TwitterConnection
    {
        // controls
        private const int maxbackoff = 450;
        private const int pagingSize = 50;
        private const int sinceIdPagingSize = 200;
        //

        // status bits
        private readonly Subject<bool> backFillCompleted = new Subject<bool>();
        private readonly Subject<bool> userStreamConnected = new Subject<bool>();
        //

        // largestSeenIds
        // ToDo: these should/could be properties on Account so they can be persisted across launches
        private long _homeTimeLineLargestSeenId;
        private long _directMessagesReceivedLargestSeenId;
        private long _directMessagesSentLargestSeenId;
        private long _retweetsOfMeLargestSeenId;
        private long _mentionsOfMeLargestSeenId;
        private long _myTweetsLargestSeenId;
        //

        readonly Subject<Tweet> _timeline = new Subject<Tweet>();
        public IObservable<Tweet> TimeLine { get { return _timeline; } }

        readonly Subject<Tweet> _retweetsOnTimeline = new Subject<Tweet>();
        public IObservable<Tweet> RetweetsOnTimeLine { get { return _retweetsOnTimeline; } }

        readonly Subject<Tweet> _mentions = new Subject<Tweet>();
        public IObservable<Tweet> Mentions { get { return _mentions; } }

        readonly Subject<Tweet> _mytweets = new Subject<Tweet>();
        public IObservable<Tweet> MyTweets { get { return _mytweets; } }

        readonly Subject<DirectMessage> _directmessages = new Subject<DirectMessage>();
        public IObservable<DirectMessage> DirectMessages { get { return _directmessages; } }

        readonly Subject<User> _usersseen = new Subject<User>();
        public IObservable<User> UsersSeen { get { return _usersseen; } }

        readonly Subject<DeleteEventStatus> _streamdeleteevent = new Subject<DeleteEventStatus>();
        public IObservable<DeleteEventStatus> StreamDeleteEvent { get { return _streamdeleteevent; } }

        private readonly List<long> _tweetIdsRegister = new List<long>();
        readonly Subject<long> _tweetsseen = new Subject<long>();
        public IObservable<long> TweetsSeen { get { return _tweetsseen; } }

        /*
         * NOTE: scrubgeo, statuswithheld, limitnotices, userwithheld and friends stream currently
         * not .Subscribed to from the UserStream
         */

        private CancellationTokenSource TwitterCommunication;

        private readonly TimeSpan _multiFetchBackoffTimer = new TimeSpan(1200);

        private bool IsUnique(Tweet t)
        {
            if (_tweetIdsRegister.Contains(t.Id)) return false;
            _tweetIdsRegister.Add(t.Id);
            return true;
        }

        private void AddToHomeTimeLine(Tweet t)
        {
            // only Publish if unique
            if (IsUnique(t))
            {
                _tweetsseen.OnNext(t.Id);
                _timeline.OnNext(t);
                _usersseen.OnNext(t.User);
            }
        }

        public void Start()
        {
            TwitterCommunication = new CancellationTokenSource();
            //
            UserStream = UserSession.UserStreamBuilder();

            // this watches the userstream, if there is a disconnect event, do something about it
            TwitterConnectionEvents.GetEvent<TwitterUserStreamDisconnectEvent>().Subscribe(ManageUserStreamDisconnect);

            // Separate stream events start 
            StartStreamEvents();

            StartUserStreams();
           
            // MORE MAGIC HAPPENS HERE
            // The Userstreams only get tweets/direct messages from the point the connection is opened. 
            // Historical tweets/direct messages have to be gathered using the traditional paging/cursoring APIs
            // (Request/Response REST).
            // but the higher level client doesnt want to worry about all that complexity.
            // in the BackfillPump, we gather these tweets/direct messages and pump them into the correct Observable
            Task.Factory.StartNew(ProcessBackfillPump);

            // If the UserStream doesn't or cannot connect, the userStreamConnected will fire
            // There is something weird (at least on my various machines) where Windows 8.1 Preview
            // cannot make a TLS3/SSL connection to userstream.twitter.com. I created this as a fallback
            userStreamConnected.Where(status => status.IsFalse()).Subscribe(StartPollingUpdates);
        }

        public void Stop()
        {
            TwitterCommunication.Cancel();
            UserStream.Stop();
            userStreamConnected.OnNext(false);
        }

        private void StartUserStreams()
        {
            // All tweets to the HomeTimeLine
            UserStream.Tweets.Subscribe(AddToHomeTimeLine);

            // MAGIC HAPPENS HERE
            // there is no specific "catch" for mentions in the Userstream, but here we can fake it!
            // Using LINQ, we can ask RX to show us incoming tweets that contain the screen name of the current user
            // then push this into the "Mentions" Observable
            UserStream.Tweets.Where(t => t.Text.ToLower().Contains(TwitterCredentials.ScreenName.ToLower())).Subscribe(_mentions.OnNext);

            // specifically grab the Retweets in the timeline and show them
            // obviously this can be added at a higher level, too.
            UserStream.Tweets.Where(t => t.RetweetedStatus != null).Subscribe(_retweetsOnTimeline.OnNext);

            // Treat Direct Messages separately
            UserStream.DirectMessages.Subscribe(_directmessages.OnNext);

            // Pull out my tweets, and publish separately
            UserStream.Tweets.Where(t => t.User.UserId == TwitterCredentials.UserID).Subscribe(_mytweets.OnNext);

            // also grab the delete events and publish
            UserStream.DeleteEvents.Subscribe(de => _streamdeleteevent.OnNext(de.DeleteEventStatus));

            UserStream.Start();
            userStreamConnected.OnNext(true);
        }

        // subscriber to the userstream disconnecting
        private void ManageUserStreamDisconnect(TwitterUserStreamDisconnectEvent disconnectEvent)
        {
            userStreamConnected.OnNext(false); // push message saying userStream is no longer connected
        }

        private void StartPollingUpdates(bool status)
        {
            // firstly wait on the backfills to complete before firing off these
            backFillCompleted.Where(st => st == true).Subscribe(s =>
            {
                // this will fire once per minute for 24 hours from init
                var observable = Observable.Interval(TimeSpan.FromMinutes(1));
                observable.Subscribe(async t =>
                {
                    _homeTimeLineLargestSeenId = await GetHomeTimeLine_Failover(_homeTimeLineLargestSeenId);
                    _directMessagesReceivedLargestSeenId = await GetDirectMessages_Received_Failover(_directMessagesReceivedLargestSeenId);
                    _directMessagesSentLargestSeenId = await GetDirectMessages_Sent_Failover(_directMessagesSentLargestSeenId);
                    _retweetsOfMeLargestSeenId = await GetRTOfMe_Failover(_retweetsOfMeLargestSeenId);
                    _mentionsOfMeLargestSeenId = await GetMentions_Failover(_mentionsOfMeLargestSeenId);
                    _myTweetsLargestSeenId = await GetMyTweets_Failover(_myTweetsLargestSeenId);
                });
            });
        }


        // FAIL-OVER TO PULL REQUESTS
        //TODO: DRY these methods with <Func> goodness

        private async Task<long> GetHomeTimeLine_Failover(long sinceid)
        {
            var largestseenid = sinceid;

            var hometl = await UserSession.GetHomeTimeline(count: sinceIdPagingSize, since_id: sinceid);
            if (!hometl.OK) return largestseenid;
            foreach (var tweet in hometl)
            {
                AddToHomeTimeLine(tweet); // use this guard
                if (tweet.Id > sinceid) largestseenid = tweet.Id;
            }
            return largestseenid;
        }

        private async Task<long> GetMentions_Failover(long sinceid)
        {
            var largestseenid = sinceid;

            var mentionsofme = await UserSession.GetMentions(count: sinceIdPagingSize, since_id: sinceid);
            if (!mentionsofme.OK) return largestseenid;
            foreach (var tweet in mentionsofme.Where(tweet => tweet.Id > sinceid))
            {
                largestseenid = tweet.Id;
                _mentions.OnNext(tweet);
            }
            return largestseenid;
        }

        private async Task<long> GetRTOfMe_Failover(long sinceid)
        {
            var largestseenid = sinceid;

            var rtofme = await UserSession.GetRetweetsOfMe(count: pagingSize, since_id: sinceid);
            if (!rtofme.OK) return largestseenid;
            foreach (var tweet in rtofme.Where(tweet => tweet.Id > sinceid))
            {
                largestseenid = tweet.Id;
                _mentions.OnNext(tweet);
            }
            return largestseenid;
        }

        private async Task<long> GetDirectMessages_Received_Failover(long sinceid)
        {
            var largestseenid = sinceid;

            var dmrecd = await UserSession.GetDirectMessages(count: pagingSize, since_id: sinceid);
            if (!dmrecd.OK) return largestseenid;
            foreach (var dm in dmrecd.Where(dm => dm.Id > sinceid))
            {
                _directmessages.OnNext(dm);
                largestseenid = dm.Id;
            }
            return largestseenid;
        }

        private async Task<long> GetDirectMessages_Sent_Failover(long sinceid)
        {
            var largestseenid = sinceid;
            
            var mysentdms = await UserSession.GetDirectMessagesSent(count: pagingSize, since_id: sinceid);
            if (!mysentdms.OK) return largestseenid;

            foreach (var dm in mysentdms.Where(dm => dm.Id > sinceid))
            {
                _directmessages.OnNext(dm);
                largestseenid = dm.Id;
            }
            return largestseenid;
        }

        private async Task<long> GetMyTweets_Failover(long sinceid)
        {
           var largestseenid = sinceid;
            
            var hometl = await UserSession.GetUserTimeline(user_id: AccountDetails.UserId, count: pagingSize, since_id: sinceid);
            if (!hometl.OK) return largestseenid;

            foreach (var tweet in hometl.Where(tweet => tweet.Id > sinceid))
            {
                _mytweets.OnNext(tweet);
                largestseenid = tweet.Id;
            }
            return largestseenid;
        }


        // BACKFILLS
        private async void ProcessBackfillPump()
        {
            var o = Observable.CombineLatest(
                Observable.Start( async () =>
                    {
                        _homeTimeLineLargestSeenId = await GetHomeTimeLine_Backfill();
                    }),
                Observable.Start( async () =>
                    {
                       _directMessagesReceivedLargestSeenId = await GetDirectMessages_Received_Backfill();
                    }),
                Observable.Start( async () =>
                    {
                        _directMessagesSentLargestSeenId = await GetDirectMessages_Sent_Backfill();
                    }),
                Observable.Start( async () =>
                    {
                        _retweetsOfMeLargestSeenId = await GetRTOfMe_Backfill();
                    }),
                Observable.Start( async () =>
                    {
                        _mentionsOfMeLargestSeenId = await GetMentions_Backfill();
                    }),
                Observable.Start( async () =>
                    {
                        _myTweetsLargestSeenId = await GetMyTweets_Backfill();
                    })
                ).Finally(() => backFillCompleted.OnNext(true));
            await o;
        }

        // these grab tweets/dms from history rather from the current stream
        //TODO: DRY these methods with <Func> goodness
        private async Task<long> GetHomeTimeLine_Backfill()
        {
            long smallestid = 0;
            long largestid = 0;
            int backfillQuota = 50;
            int backofftimer = 30;

            do
            {
                var hometl = await UserSession.GetHomeTimeline(count: pagingSize, max_id: smallestid);
                if (hometl.OK)
                {
                    smallestid = long.MaxValue;
                    if (hometl.Count < backfillQuota) backfillQuota = hometl.Count;
                    foreach (var tweet in hometl)
                    {
                        if (tweet.Id < smallestid) smallestid = tweet.Id;
                        if (tweet.Id > largestid) largestid = tweet.Id;
                        AddToHomeTimeLine(tweet);
                        backfillQuota--;
                    }
                    await Task.Delay(_multiFetchBackoffTimer);
                }
                else
                {
                    // The Backoff will trigger 7 times before just giving up
                    // once at 30s, 60s, 1m, 2m, 4m, 8m and then 16m
                    // note that the last call into this will be 1m above the 15 "rate limit reset" window 
                    await Task.Delay(TimeSpan.FromSeconds(backofftimer));
                    if (backofftimer > maxbackoff)
                        break;
                    backofftimer = backofftimer * 2;
                }
            } while (backfillQuota > 0);
            return largestid;
        }

        private async Task<long> GetMentions_Backfill()
        {
            long smallestid = 0;
            long largestid = 0;
            int backfillQuota = 50;
            int backofftimer = 30;

            do
            {
                var mentionsofme = await UserSession.GetMentions(count: pagingSize, max_id: smallestid);
                if (mentionsofme.OK)
                {
                    smallestid = long.MaxValue;
                    if (mentionsofme.Count < backfillQuota) backfillQuota = mentionsofme.Count;
                    foreach (var tweet in mentionsofme)
                    {
                        _mentions.OnNext(tweet);
                        if (tweet.Id < smallestid) smallestid = tweet.Id;
                        if (tweet.Id > largestid) largestid = tweet.Id;
                        backfillQuota--;
                    }
                    await Task.Delay(_multiFetchBackoffTimer);
                }
                else
                {
                    // The Backoff will trigger 7 times before just giving up
                    // once at 30s, 60s, 1m, 2m, 4m, 8m and then 16m
                    // note that the last call into this will be 1m above the 15 "rate limit reset" window 
                    await Task.Delay(TimeSpan.FromSeconds(backofftimer));
                    if (backofftimer > maxbackoff)
                        break;
                    backofftimer = backofftimer * 2;
                }
            } while (backfillQuota > 0);
            return largestid;
        }

        private async Task<long> GetRTOfMe_Backfill()
        {
            long smallestid = 0;
            long largestid = 0;
            int backfillQuota = 50;
            int backofftimer = 30;

            do
            {
                var rtofme = await UserSession.GetRetweetsOfMe(count: pagingSize, max_id: smallestid);
                if (rtofme.OK)
                {
                    smallestid = long.MaxValue;
                    if (rtofme.Count < backfillQuota) backfillQuota = rtofme.Count;
                    foreach (var tweet in rtofme)
                    {
                        _mentions.OnNext(tweet);
                        if (tweet.Id < smallestid) smallestid = tweet.Id;
                        if (tweet.Id > largestid) largestid = tweet.Id;
                        backfillQuota--;
                    }
                    await Task.Delay(_multiFetchBackoffTimer);
                }
                else
                {
                    // The Backoff will trigger 7 times before just giving up
                    // once at 30s, 60s, 1m, 2m, 4m, 8m and then 16m
                    // note that the last call into this will be 1m above the 15 "rate limit reset" window 
                    await Task.Delay(TimeSpan.FromSeconds(backofftimer));
                    if (backofftimer > maxbackoff)
                        break;
                    backofftimer = backofftimer * 2;
                }
            } while (backfillQuota > 0);
            return largestid;
        }

        private async Task<long> GetDirectMessages_Received_Backfill()
        {
            long smallestid = 0;
            long largestid = 0;
            int backfillQuota = 50;
            int backofftimer = 30;

            do
            {
                var dmrecd = await UserSession.GetDirectMessages(count: pagingSize, max_id: smallestid);
                if (dmrecd.OK)
                {
                    smallestid = long.MaxValue;
                    // if the Count of items returned is less than the backfill quota, we have run out of messages.
                    // this will decrement the backfill quota to zero, falling out of the loop
                    if (dmrecd.Count < backfillQuota) backfillQuota = dmrecd.Count;
                    foreach (var dm in dmrecd)
                    {
                        _directmessages.OnNext(dm);
                        if (dm.Id < smallestid) smallestid = dm.Id;
                        if (dm.Id > largestid) largestid = dm.Id;
                        backfillQuota--;
                    }
                    await Task.Delay(_multiFetchBackoffTimer);
                }
                else
                {
                    // The Backoff will trigger 7 times before just giving up
                    // once at 30s, 60s, 1m, 2m, 4m, 8m and then 16m
                    // note that the last call into this will be 1m above the 15 "rate limit reset" window 
                    await Task.Delay(TimeSpan.FromSeconds(backofftimer));
                    if (backofftimer > maxbackoff)
                        break;
                    backofftimer = backofftimer * 2;
                }
            } while (backfillQuota > 0);
            return largestid;
        }

        private async Task<long> GetDirectMessages_Sent_Backfill()
        {
            long smallestid = 0;
            long largestid = 0;
            int backfillQuota = 50;
            int backofftimer = 30;

            do
            {
                var mysentdms = await UserSession.GetDirectMessagesSent(count: pagingSize, max_id: smallestid);
                if (mysentdms.OK)
                {
                    smallestid = long.MaxValue;
                    if (mysentdms.Count < backfillQuota) backfillQuota = mysentdms.Count;
                    foreach (var dm in mysentdms)
                    {
                        _directmessages.OnNext(dm);
                        if (dm.Id < smallestid) smallestid = dm.Id;
                        if (dm.Id > largestid) largestid = dm.Id;
                        backfillQuota--;
                    }
                    await Task.Delay(_multiFetchBackoffTimer);
                }
                else
                {
                    // The Backoff will trigger 7 times before just giving up
                    // once at 30s, 60s, 1m, 2m, 4m, 8m and then 16m
                    // note that the last call into this will be 1m above the 15 "rate limit reset" window 
                    await Task.Delay(TimeSpan.FromSeconds(backofftimer));
                    if (backofftimer > maxbackoff)
                        break;
                    backofftimer = backofftimer * 2;
                }
            } while (backfillQuota > 0);
            return largestid;
        }

        private async Task<long> GetMyTweets_Backfill()
        {
            long smallestid = 0;
            long largestid = 0;
            int backfillQuota = 50;
            int backofftimer = 30;

            do
            {
                var hometl = await UserSession.GetUserTimeline(user_id:AccountDetails.UserId, count: pagingSize, max_id: smallestid);
                if (hometl.OK)
                {
                    smallestid = long.MaxValue;
                    if (hometl.Count < backfillQuota) backfillQuota = hometl.Count;
                    foreach (var tweet in hometl)
                    {
                        _mytweets.OnNext(tweet);
                        if (tweet.Id < smallestid) smallestid = tweet.Id;
                        if (tweet.Id > largestid) largestid = tweet.Id;
                        backfillQuota--;
                    }
                    await Task.Delay(_multiFetchBackoffTimer);
                }
                else
                {
                    // The Backoff will trigger 7 times before just giving up
                    // once at 30s, 60s, 1m, 2m, 4m, 8m and then 16m
                    // note that the last call into this will be 1m above the 15 "rate limit reset" window 
                    await Task.Delay(TimeSpan.FromSeconds(backofftimer));
                    if (backofftimer > maxbackoff)
                        break;
                    backofftimer = backofftimer * 2;
                }
            } while (backfillQuota > 0);
            return largestid;
        }

    }
}