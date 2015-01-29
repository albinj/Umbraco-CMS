﻿using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Persistence.Repositories;
using Umbraco.Core.Sync;
using Umbraco.Tests.DistributedCache;
using Umbraco.Tests.Services;
using Umbraco.Tests.TestHelpers;
using Umbraco.Tests.TestHelpers.Entities;
using Umbraco.Web.Cache;

namespace Umbraco.Tests.Integration
{
    [DatabaseTestBehavior(DatabaseBehavior.NewDbFileAndSchemaPerTest)]
    [TestFixture, RequiresSTA]
    public class ContentEventsTests : BaseServiceTest
    {
        #region Setup

        public override void Initialize()
        {
            ServerRegistrarResolver.Current = new ServerRegistrarResolver(new DistributedCacheTests.TestServerRegistrar()); // localhost-only
            ServerMessengerResolver.Current = new ServerMessengerResolver(new DefaultServerMessenger());
            CacheRefreshersResolver.Current = new CacheRefreshersResolver(() => new[]
            {
                typeof(ContentTypeCacheRefresher),
                typeof(ContentCacheRefresher),

                typeof(MacroCacheRefresher)
            });

            base.Initialize();

            _h1 = new CacheRefresherEventHandler();
            _h1.OnApplicationStarted(null, ApplicationContext);

            _events = new List<EventInstance>();

            ContentRepository.RefreshedEntity += ContentRepositoryRefreshed;
            ContentRepository.RemovedEntity += ContentRepositoryRemoved;
            ContentRepository.RemovedVersion += ContentRepositoryRemovedVersion;

            ContentCacheRefresher.CacheUpdated += ContentCacheUpdated;
        }

        public override void TearDown()
        {
            base.TearDown();

            _h1.ClearEvents();

            // clear ALL events

            ContentRepository.RefreshedEntity -= ContentRepositoryRefreshed;
            ContentRepository.RemovedEntity -= ContentRepositoryRemoved;
            ContentRepository.RemovedVersion -= ContentRepositoryRemovedVersion;

            ContentCacheRefresher.CacheUpdated -= ContentCacheUpdated;
        }

        private CacheRefresherEventHandler _h1;
        private IList<EventInstance> _events;
        private int _msgCount;

        private void ResetEvents()
        {
            _events = new List<EventInstance>();
            _msgCount = 0;
            LogHelper.Debug<ContentEventsTests>("RESET EVENTS");
        }

        private IContent CreateBranch()
        {
            var contentType = MockedContentTypes.CreateSimpleContentType("whatever", "Whatever");
            contentType.Key = Guid.NewGuid();
            ServiceContext.ContentTypeService.Save(contentType);

            var content1 = MockedContent.CreateSimpleContent(contentType, "Content1");
            ServiceContext.ContentService.SaveAndPublishWithStatus(content1);

            // 2 (published)
            // .1 (published)
            // .2 (not published)
            var content2 = MockedContent.CreateSimpleContent(contentType, "Content2", content1);
            ServiceContext.ContentService.SaveAndPublishWithStatus(content2);
            var content21 = MockedContent.CreateSimpleContent(contentType, "Content21", content2);
            ServiceContext.ContentService.SaveAndPublishWithStatus(content21);
            var content22 = MockedContent.CreateSimpleContent(contentType, "Content22", content2);
            ServiceContext.ContentService.Save(content22);

            // 3 (not published)
            // .1 (not published)
            // .2 (not published)
            var content3 = MockedContent.CreateSimpleContent(contentType, "Content3", content1);
            ServiceContext.ContentService.Save(content3);
            var content31 = MockedContent.CreateSimpleContent(contentType, "Content31", content3);
            ServiceContext.ContentService.Save(content31);
            var content32 = MockedContent.CreateSimpleContent(contentType, "Content32", content3);
            ServiceContext.ContentService.Save(content32);

            // 4 (published + saved)
            // .1 (published)
            // .2 (not published)
            var content4 = MockedContent.CreateSimpleContent(contentType, "Content4", content1);
            ServiceContext.ContentService.SaveAndPublishWithStatus(content4);
            content4.Name = "Content4X";
            ServiceContext.ContentService.Save(content4);
            var content41 = MockedContent.CreateSimpleContent(contentType, "Content41", content4);
            ServiceContext.ContentService.SaveAndPublishWithStatus(content41);
            var content42 = MockedContent.CreateSimpleContent(contentType, "Content42", content4);
            ServiceContext.ContentService.Save(content42);

            // 5 (not published)
            // .1 (published)
            // .2 (not published)
            var content5 = MockedContent.CreateSimpleContent(contentType, "Content5", content1);
            ServiceContext.ContentService.SaveAndPublishWithStatus(content5);
            var content51 = MockedContent.CreateSimpleContent(contentType, "Content51", content5);
            ServiceContext.ContentService.SaveAndPublishWithStatus(content51);
            var content52 = MockedContent.CreateSimpleContent(contentType, "Content52", content5);
            ServiceContext.ContentService.Save(content52);
            ServiceContext.ContentService.UnPublish(content5);

            return content1;
        }

        #endregion

        #region Events tracer

        private class EventInstance
        {
            public int Msg { get; set; }
            public string Sender { get; set; }
            public string Name { get; set; }
            public string Args { get; set; }
            public object EventArgs { get; set; }

            public override string ToString()
            {
                return "{0:000}: {1}/{2}/{3}".FormatWith(Msg, Sender.Replace(" ", ""), Name, Args);
            }
        }

        private void ContentRepositoryRefreshed(VersionableRepositoryBase<int, IContent> sender, VersionableRepositoryBase<int, IContent>.EntityChangeEventArgs args)
        {
            var e = new EventInstance
            {
                Msg = _msgCount++,
                Sender = "ContentRepository",
                Name = "Refresh",
                Args = string.Join(",", args.Entities.Select(x => (x.Published ? "p" : "u") + "-" + x.Id))
            };
            _events.Add(e);
        }

        private void ContentRepositoryRemoved(VersionableRepositoryBase<int, IContent> sender, VersionableRepositoryBase<int, IContent>.EntityChangeEventArgs args)
        {
            var e = new EventInstance
            {
                Msg = _msgCount++,
                Sender = "ContentRepository",
                EventArgs = args,
                Name = "Remove",
                Args = string.Join(",", args.Entities.Select(x => (x.Published ? "p" : "u") + x.Id))
            };
            _events.Add(e);
        }

        private void ContentRepositoryRemovedVersion(VersionableRepositoryBase<int, IContent> sender, VersionableRepositoryBase<int, IContent>.VersionChangeEventArgs args)
        {
            var e = new EventInstance
            {
                Msg = _msgCount++,
                Sender = "ContentRepository",
                EventArgs = args,
                Name = "RemoveVersion",
                Args = string.Join(",", args.Versions.Select(x => "{0}:{1}".FormatWith(x.Item1, x.Item2)))
            };
            _events.Add(e);
        }

        private void ContentCacheUpdated(ContentCacheRefresher sender, CacheRefresherEventArgs args)
        {
            if (args.MessageType != MessageType.RefreshByJson)
                throw new NotSupportedException();

            foreach (var payload in ContentCacheRefresher.Deserialize((string) args.MessageObject))
            {
                var e = new EventInstance
                {
                    Msg = _msgCount,
                    Sender = sender.Name,
                    EventArgs = payload,
                    Name = payload.Action.ToString().Replace(" ", ""),
                    Args = payload.Id.ToInvariantString()
                };
                _events.Add(e);
            }

            _msgCount++;
        }

        /*
        private void PageCacheUpdated(PageCacheRefresher sender, CacheRefresherEventArgs args)
        {
            var e = new EventInstance
            {
                Sender = sender.Name,
                EventArgs = args,
                Name = args.MessageType.ToString()
            };
            switch (args.MessageType)
            {
                case MessageType.RefreshById:
                case MessageType.RemoveById:
                    e.Args = ((int)args.MessageObject).ToInvariantString();
                    break;
                case MessageType.RefreshByInstance:
                case MessageType.RemoveByInstance:
                    e.Args = ((IContent)args.MessageObject).Id.ToInvariantString();
                    break;
                case MessageType.RefreshByJson:
                    // is NOT a JSON refresher!
                    break;
            }
            _events.Add(e);
        }

        private void UnpublishedPageCacheUpdated(UnpublishedPageCacheRefresher sender, CacheRefresherEventArgs args)
        {
            var e = new EventInstance
            {
                Sender = sender.Name,
                EventArgs = args,
                Name = args.MessageType.ToString()
            };
            switch (args.MessageType)
            {
                case MessageType.RefreshById:
                case MessageType.RemoveById:
                    e.Args = ((int)args.MessageObject).ToInvariantString();
                    break;
                case MessageType.RefreshByInstance:
                case MessageType.RemoveByInstance:
                    e.Args = ((IContent)args.MessageObject).Id.ToInvariantString();
                    break;
                case MessageType.RefreshByJson:
                    var json = (string)args.MessageObject;
                    e.Args = string.Join(",",
                        UnpublishedPageCacheRefresher.DeserializeFromJsonPayload(json)
                            .Select(payload => "{0}{1}".FormatWith(
                                payload.Operation == UnpublishedPageCacheRefresher.OperationType.Deleted ? "-" : "+",
                                payload.Id)));
                    break;
            }
            _events.Add(e);
        }
        */

        #endregion

        #region Save, Publish & UnPublish single content

        [Test]
        public void HasInitialContent()
        {
            Assert.AreEqual(4, ServiceContext.ContentService.Count());
        }

        [Test]
        public void SaveUnpublishedContent()
        {
            // rule: when a content is saved,
            // - repository : refresh (u)
            // - content cache :: refresh newest

            var content = ServiceContext.ContentService.GetRootContent().FirstOrDefault();
            Assert.IsNotNull(content);

            ResetEvents();
            content.Name = "changed";
            ServiceContext.ContentService.Save(content);

            Assert.AreEqual(2, _msgCount);
            Assert.AreEqual(2, _events.Count);
            var i = 0;
            var m = 0;            
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/u-{1}".FormatWith(m, content.Id), _events[i++].ToString());
            m++;
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshNewest/{1}".FormatWith(m, content.Id), _events[i].ToString());
        }

        [Test]
        public void SavePublishedContent()
        {
            // rule: when a content is saved,
            // - repository : refresh (u)
            // - content cache :: refresh newest

            var content = ServiceContext.ContentService.GetRootContent().FirstOrDefault();
            Assert.IsNotNull(content);
            ServiceContext.ContentService.SaveAndPublishWithStatus(content);

            ResetEvents();
            content.Name = "changed";
            ServiceContext.ContentService.Save(content);

            Assert.AreEqual(2, _msgCount);
            Assert.AreEqual(2, _events.Count);
            var i = 0;
            var m = 0;
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/u-{1}".FormatWith(m, content.Id), _events[i++].ToString());
            m++;
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshNewest/{1}".FormatWith(m, content.Id), _events[i].ToString());
        }

        [Test]
        public void SaveAndPublishContent()
        {
            // rule: when a content is saved&published,
            // - repository : refresh (p)
            // - content cache :: refresh published, newest

            var content = ServiceContext.ContentService.GetRootContent().FirstOrDefault();
            Assert.IsNotNull(content);

            ResetEvents();
            content.Name = "changed";
            ServiceContext.ContentService.SaveAndPublishWithStatus(content);

            Assert.AreEqual(2, _msgCount);
            Assert.AreEqual(3, _events.Count);
            var i = 0;
            var m = 0;
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/p-{1}".FormatWith(m, content.Id), _events[i++].ToString());
            m++;
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshNewest/{1}".FormatWith(m, content.Id), _events[i++].ToString());
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content.Id), _events[i].ToString());
        }

        [Test]
        public void PublishContent()
        {
            // rule: when a content is published,
            // - repository : refresh (p)
            // - published page cache :: refresh
            // note: whenever the published cache is refreshed, subscribers must
            // assume that the unpublished cache is also refreshed, with the same
            // values, and deal with it.

            var content = ServiceContext.ContentService.GetRootContent().FirstOrDefault();
            Assert.IsNotNull(content);

            ResetEvents();
            content.Name = "changed";
            ServiceContext.ContentService.PublishWithStatus(content);

            Assert.AreEqual(2, _msgCount);
            Assert.AreEqual(3, _events.Count);
            var i = 0;
            var m = 0;
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/p-{1}".FormatWith(m, content.Id), _events[i++].ToString());
            m++;
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshNewest/{1}".FormatWith(m, content.Id), _events[i++].ToString());
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content.Id), _events[i].ToString());
        }

        [Test]
        public void UnpublishContent()
        {
            // rule: when a content is unpublished,
            // - repository : refresh (u)
            // - content cache :: refresh newest, remove published

            var content = ServiceContext.ContentService.GetRootContent().FirstOrDefault();
            Assert.IsNotNull(content);
            ServiceContext.ContentService.SaveAndPublishWithStatus(content);

            ResetEvents();
            ServiceContext.ContentService.UnPublish(content);

            Assert.AreEqual(2, _msgCount);
            Assert.AreEqual(3, _events.Count);
            var i = 0;
            var m = 0;
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/u-{1}".FormatWith(m, content.Id), _events[i++].ToString());
            m++;
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshNewest/{1}".FormatWith(m, content.Id), _events[i++].ToString());
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RemovePublished/{1}".FormatWith(m, content.Id), _events[i].ToString());
        }

        [Test]
        public void UnpublishContentWithChanges()
        {
            // rule: when a content is unpublished,
            // - repository : refresh (u)
            // - content cache :: refresh newest, remove published

            var content = ServiceContext.ContentService.GetRootContent().FirstOrDefault();
            Assert.IsNotNull(content);
            ServiceContext.ContentService.SaveAndPublishWithStatus(content);
            content.Name = "changed";
            ServiceContext.ContentService.Save(content);

            ResetEvents();
            ServiceContext.ContentService.UnPublish(content);

            Assert.AreEqual(2, _msgCount);
            Assert.AreEqual(3, _events.Count);
            var i = 0;
            var m = 0;
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/u-{1}".FormatWith(m, content.Id), _events[i++].ToString());
            m++;
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshNewest/{1}".FormatWith(m, content.Id), _events[i++].ToString());
            Assert.AreEqual("changed", ServiceContext.ContentService.GetById(((ContentCacheRefresher.JsonPayload)_events[i - 1].EventArgs).Id).Name);
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RemovePublished/{1}".FormatWith(m, content.Id), _events[i].ToString());
        }

        #endregion

        #region Publish & UnPublish branch

        [Test]
        public void UnpublishContentBranch()
        {
            // rule: when a content branch is unpublished,
            // - repository :: refresh root (u)
            // - unpublished page cache :: refresh root
            // - published page cache :: remove root
            // note: subscribers must take care of the hierarchy and unpublish
            // the whole branch by themselves. Examine does it in UmbracoContentIndexer,
            // content caches have to do it too... wondering whether we should instead
            // trigger RemovePublished for all of the removed content?

            var content1 = CreateBranch();

            ResetEvents();
            ServiceContext.ContentService.UnPublish(content1);

            Assert.AreEqual(2, _msgCount);
            Assert.AreEqual(3, _events.Count);
            var i = 0;
            var m = 0;
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/u-{1}".FormatWith(m, content1.Id), _events[i++].ToString());
            m++;
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshNewest/{1}".FormatWith(m, content1.Id), _events[i++].ToString());
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RemovePublished/{1}".FormatWith(m, content1.Id), _events[i].ToString());
        }

        [Test]
        public void PublishContentBranch()
        {
            // rule: when a content branch is published,
            // - repository :: refresh root (p)
            // - published page cache :: refresh root & descendants, database (level, sortOrder) order

            var content1 = CreateBranch();
            ServiceContext.ContentService.UnPublish(content1);

            ResetEvents();
            ServiceContext.ContentService.PublishWithStatus(content1);

            Assert.AreEqual(2, _msgCount);
            Assert.AreEqual(7, _events.Count);
            var i = 0;
            var m = 0;
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/p-{1}".FormatWith(m, content1.Id), _events[i++].ToString());
            m++;
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshNewest/{1}".FormatWith(m, content1.Id), _events[i++].ToString()); // repub content1
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content1.Id), _events[i++].ToString()); // repub content1
            var content1C = content1.Children().ToArray();
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content1C[0].Id), _events[i++].ToString()); // repub content1.content2
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content1C[2].Id), _events[i++].ToString()); // repub content1.content4
            var c = ServiceContext.ContentService.GetPublishedVersion(((ContentCacheRefresher.JsonPayload)_events[i - 1].EventArgs).Id);
            Assert.IsTrue(c.Published); // get the published one
            Assert.AreEqual("Content4", c.Name); // published has old name
            var content2C = content1C[0].Children().ToArray();
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content2C[0].Id), _events[i++].ToString()); // repub content1.content2.content21
            var content4C = content1C[2].Children().ToArray();
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content4C[0].Id), _events[i].ToString()); // repub content1.content4.content41
        }

        [Test]
        public void PublishContentBranchWithPublishedChildren()
        {
            // rule?

            var content1 = CreateBranch();
            ServiceContext.ContentService.UnPublish(content1);

            ResetEvents();
            ServiceContext.ContentService.PublishWithChildrenWithStatus(content1, 0, false);

            Assert.AreEqual(6, _msgCount);
            Assert.AreEqual(10, _events.Count); // fixme - should be 11
            var i = 0;
            var m = 0;
            var content1C = content1.Children().ToArray();
            var content2C = content1C[0].Children().ToArray();
            var content4C = content1C[2].Children().ToArray();
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/p-{1}".FormatWith(m++, content1.Id), _events[i++].ToString());
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/p-{1}".FormatWith(m++, content1C[0].Id), _events[i++].ToString());
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/p-{1}".FormatWith(m++, content1C[2].Id), _events[i++].ToString());
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/p-{1}".FormatWith(m++, content2C[0].Id), _events[i++].ToString());
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/p-{1}".FormatWith(m++, content4C[0].Id), _events[i++].ToString());
            m++;
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshNewest/{1}".FormatWith(m, content1.Id), _events[i++].ToString()); // repub content1
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content1.Id), _events[i++].ToString()); // repub content1
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content1C[0].Id), _events[i++].ToString()); // repub content1.content2
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content1C[2].Id), _events[i++].ToString()); // repub content1.content4
            var c = ServiceContext.ContentService.GetPublishedVersion(((ContentCacheRefresher.JsonPayload)_events[i - 1].EventArgs).Id);
            Assert.IsTrue(c.Published); // get the published one
            Assert.AreEqual("Content4", c.Name); // published has old name
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content2C[0].Id), _events[i++].ToString()); // repub content1.content2.content21
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content4C[0].Id), _events[i].ToString()); // repub content1.content4.content41
        }

        [Test]
        [Ignore("Not Implemented")]
        public void PublishContentBranchWithAllChildren()
        {
            // rule?

            var content1 = CreateBranch();
            ServiceContext.ContentService.UnPublish(content1);

            ResetEvents();
            ServiceContext.ContentService.PublishWithChildrenWithStatus(content1, 0, true);

            Assert.AreEqual(2, _msgCount);
            Assert.AreEqual(6, _events.Count);
            var i = 0;
            var m = 0;
            Assert.AreEqual("{0:000}: ContentRepository/Refresh/p-{1}".FormatWith(m, content1.Id), _events[i++].ToString());
            m++;
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished,RefreshNewest/{1}".FormatWith(m, content1.Id), _events[i++].ToString()); // repub content1
            var content1C = content1.Children().ToArray();
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content1C[0].Id), _events[i++].ToString()); // repub content1.content2
            var content2C = content1C[0].Children().ToArray();
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content2C[0].Id), _events[i++].ToString()); // repub content1.content2.content21
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content1C[2].Id), _events[i++].ToString()); // repub content1.content4
            var c = ServiceContext.ContentService.GetPublishedVersion(((ContentCacheRefresher.JsonPayload)_events[i - 1].EventArgs).Id);
            Assert.IsTrue(c.Published); // get the published one
            Assert.AreEqual("Content4", c.Name); // published has old name
            var content4C = content1C[2].Children().ToArray();
            Assert.AreEqual("{0:000}: ContentCacheRefresher/RefreshPublished/{1}".FormatWith(m, content4C[0].Id), _events[i].ToString()); // repub content1.content4.content41
        }

        #endregion

        #region Sort

        [Test]
        public void Sort()
        {
            // rule: when sorting,
            // fixme
            // - repository :: refresh (each modified content)

            var content1 = CreateBranch();
            var content1C = content1.Children().ToArray();
            Assert.AreEqual(3, content1C.Length);
            var content1Csorted = new[] { content1C[2], content1C[0], content1C[1] };

            _events.Clear();
            ServiceContext.ContentService.Sort(content1Csorted);

            var content1Cagain = content1.Children().ToArray();
            Assert.AreEqual(3, content1Cagain.Length);
            Assert.AreEqual(content1C[0].Id, content1Cagain[1].Id);
            Assert.AreEqual(content1C[1].Id, content1Cagain[2].Id);
            Assert.AreEqual(content1C[2].Id, content1Cagain[0].Id);

            Assert.AreEqual(0, _msgCount);
            Assert.AreEqual(0, _events.Count);
            var i = 0;
            var m = 0;
            //Assert.AreEqual("ContentRepository/Refresh/p" + content1.Id, _events[i++].ToString());
            //Assert.AreEqual("PageRefresher/RefreshByInstance/" + content1.Id, _events[i++].ToString()); // repub content1
            //var content1C = content1.Children().ToArray();
            //Assert.AreEqual("PageRefresher/RefreshByInstance/" + content1C[0].Id, _events[i++].ToString()); // repub content1.content2
            //Assert.AreEqual("PageRefresher/RefreshByInstance/" + content1C[2].Id, _events[i++].ToString()); // repub content1.content4
            //Assert.IsTrue(((IContent)((CacheRefresherEventArgs)_events[i - 1].EventArgs).MessageObject).Published); // get the published one
            //Assert.AreEqual("Content4", ((IContent)((CacheRefresherEventArgs)_events[i - 1].EventArgs).MessageObject).Name); // published has old name
            //var content2C = content1C[0].Children().ToArray();
            //Assert.AreEqual("PageRefresher/RefreshByInstance/" + content2C[0].Id, _events[i++].ToString()); // repub content1.content2.content21
            //var content4C = content1C[2].Children().ToArray();
            //Assert.AreEqual("PageRefresher/RefreshByInstance/" + content4C[0].Id, _events[i++].ToString()); // repub content1.content4.content41
        }
        
        #endregion

        #region Misc

        [Test]
        public void ContentRemembers()
        {
            var content = ServiceContext.ContentService.GetRootContent().FirstOrDefault();
            Assert.IsNotNull(content);

            ServiceContext.ContentService.Save(content);
            Assert.IsFalse(content.IsPropertyDirty("Published"));
            Assert.IsFalse(content.WasPropertyDirty("Published"));

            ServiceContext.ContentService.PublishWithStatus(content);
            Assert.IsFalse(content.IsPropertyDirty("Published"));
            Assert.IsTrue(content.WasPropertyDirty("Published")); // has just been published

            ServiceContext.ContentService.PublishWithStatus(content);
            Assert.IsFalse(content.IsPropertyDirty("Published"));
            Assert.IsFalse(content.WasPropertyDirty("Published")); // was published already
        }

        #endregion

        #region TODO

        // trash & untrash a content
        // trash & untrash a branch
        // move a content
        // move a branch
        // sort
        // rollback

        // all content type events

        #endregion
    }
}