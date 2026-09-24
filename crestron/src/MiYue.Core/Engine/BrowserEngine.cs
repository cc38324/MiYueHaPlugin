using System;
using System.Collections.Generic;
using System.Globalization;
using MiYue.Core.Didl;
using MiYue.Core.Model;
using MiYue.Core.Soap;
using MiYue.Core.Upnp;
using S = MiYue.Core.Engine.BrowserSignals;

namespace MiYue.Core.Engine
{
    public enum ListKind
    {
        None,
        Liked,
        Songlists,
        Boards,
        Radios,
        Queue,
        Scenes,
        SonglistTracks,
    }

    /// <summary>One row of a browse list.</summary>
    public sealed class BrowseEntry
    {
        public string Text = string.Empty;
        public string Sub = string.Empty;
        public string Icon = string.Empty;
        /// <summary>Songlist / board / scene id (for drill-down / execute).</summary>
        public string Id = string.Empty;
        public DidlItem Track;
    }

    /// <summary>
    /// Page-based list browsing for touch-panel Smart Graphics lists (favorites, songlists, boards, radios,
    /// queue, scenes), attached to a player of the same program by its IP address.
    /// All reads are login-free device-local MiyueLibrary / MiyueQueue / MiyueSensor calls (HA browse.py);
    /// playing uses ReplaceQueue (auto-plays StartingIndex) - except the current queue, which uses SeekToTrack.
    /// Public entry points post onto the strand.
    /// </summary>
    public sealed class BrowserEngine
    {
        public const int FindPlayerRetryMs = 2000;
        public const int QueueRefreshDelayMs = 300;

        private readonly Runtime _rt;
        private readonly OutputCache _out;
        private string _playerKey = string.Empty;
        private int _pageSize = 10;
        private bool _running;
        private PlayerEngine _player;
        private ITimerHandle _findTimer;
        private ITimerHandle _queueRefreshTimer;

        private ListKind _kind = ListKind.None;
        private string _title = string.Empty;
        private List<BrowseEntry> _entries = new List<BrowseEntry>();
        private string _didl = string.Empty;        // verbatim track-list DIDL (fed back to ReplaceQueue)
        private int _page;                          // 0-based
        private int _total;
        private int _queueCurrent = -1;
        private bool _busy;
        private string _status = string.Empty;
        private int _loadId;

        // one level of drill-down (songlists / boards -> tracks)
        private ListKind _parentKind = ListKind.None;
        private int _parentPage;
        private string _songlistId = string.Empty;
        private string _songlistName = string.Empty;

        public static readonly Dictionary<ListKind, string> Titles = new Dictionary<ListKind, string>
        {
            { ListKind.Liked, "我喜欢的 Liked" },
            { ListKind.Songlists, "歌单 Songlists" },
            { ListKind.Boards, "榜单 Boards" },
            { ListKind.Radios, "电台 Radios" },
            { ListKind.Queue, "当前队列 Queue" },
            { ListKind.Scenes, "情景 Scenes" },
        };

        public BrowserEngine(Runtime rt)
        {
            if (rt == null) throw new ArgumentNullException("rt");
            _rt = rt;
            _out = new OutputCache(rt.Log);
        }

        public ListKind Kind
        {
            get { return _kind; }
        }

        public int Page
        {
            get { return _page; }
        }

        public int Total
        {
            get { return _total; }
        }

        public IList<BrowseEntry> Entries
        {
            get { return _entries.AsReadOnly(); }
        }

        public OutputCache Outputs
        {
            get { return _out; }
        }

        public int PageSize
        {
            get { return _pageSize; }
        }

        // ---- thread-safe API ----------------------------------------------------------------------
        public void Start(string playerIp, int pageSize, IOutputSink sink)
        {
            _rt.Strand.Post(() => DoStart(playerIp, pageSize, sink));
        }

        public void Stop()
        {
            _rt.Strand.Post(DoStop);
        }

        public void Digital(ushort index, bool value)
        {
            _rt.Strand.Post(() => OnDigital(index, value));
        }

        public void Analog(ushort index, ushort value)
        {
            _rt.Strand.Post(() => OnAnalog(index, value));
        }

        // ---- lifecycle ------------------------------------------------------------------------------
        private void DoStart(string playerIp, int pageSize, IOutputSink sink)
        {
            DoStop();
            _playerKey = (playerIp ?? string.Empty).Trim();
            _pageSize = pageSize <= 0 ? 10 : Math.Min(S.MaxPageSize, pageSize);
            _out.Sink = sink;
            _out.Resync();
            _running = true;
            _kind = ListKind.None;
            _entries = new List<BrowseEntry>();
            _status = string.Empty;
            FindPlayer();
            Publish();
        }

        private void DoStop()
        {
            _running = false;
            if (_findTimer != null)
            {
                _findTimer.Cancel();
                _findTimer = null;
            }
            if (_queueRefreshTimer != null)
            {
                _queueRefreshTimer.Cancel();
                _queueRefreshTimer = null;
            }
            Attach(null);
        }

        private void FindPlayer()
        {
            if (!_running) return;
            var p = _rt.FindPlayer(_playerKey);
            if (p != _player) Attach(p);
            if (_player == null)
            {
                _status = "等待播放器 / Waiting for player " + _playerKey;
                if (_findTimer == null)
                {
                    _findTimer = _rt.After(FindPlayerRetryMs, () =>
                    {
                        _findTimer = null;
                        FindPlayer();
                        Publish();
                    });
                }
            }
            else if (_status.StartsWith("等待播放器", StringComparison.Ordinal))
            {
                _status = string.Empty;
            }
        }

        private void Attach(PlayerEngine p)
        {
            if (_player != null) _player.QueueStateChanged -= OnQueueChanged;
            _player = p;
            if (_player != null) _player.QueueStateChanged += OnQueueChanged;
        }

        /// <summary>The attached player, re-resolved in case its module was restarted.</summary>
        private PlayerEngine Player
        {
            get
            {
                if (_player == null || !_rt.Players.Contains(_player)) FindPlayer();
                return _player;
            }
        }

        private void OnQueueChanged()
        {
            if (_kind != ListKind.Queue || !_running) return;
            if (_queueRefreshTimer != null) return;
            _queueRefreshTimer = _rt.After(QueueRefreshDelayMs, () =>
            {
                _queueRefreshTimer = null;
                if (_kind == ListKind.Queue) LoadQueuePage(_page);
            });
        }

        // ---- inputs ---------------------------------------------------------------------------------
        private void OnDigital(ushort index, bool value)
        {
            if (!value || !_running) return;
            if (index > S.DiItemBase && index <= S.DiItemBase + S.MaxPageSize)
            {
                Select(index - S.DiItemBase);
                return;
            }
            switch (index)
            {
                case S.DiListLiked: Open(ListKind.Liked); break;
                case S.DiListSonglists: Open(ListKind.Songlists); break;
                case S.DiListBoards: Open(ListKind.Boards); break;
                case S.DiListRadios: Open(ListKind.Radios); break;
                case S.DiListQueue: Open(ListKind.Queue); break;
                case S.DiListScenes: Open(ListKind.Scenes); break;
                case S.DiPageNext: GotoPage(_page + 1); break;
                case S.DiPagePrev: GotoPage(_page - 1); break;
                case S.DiPageFirst: GotoPage(0); break;
                case S.DiBack: Back(); break;
                case S.DiPlayAll: PlayAll(); break;
                case S.DiRefresh: Reload(); break;
            }
        }

        private void OnAnalog(ushort index, ushort value)
        {
            if (!_running) return;
            switch (index)
            {
                case S.AiItemClicked:
                    if (value >= 1) Select(value);
                    break;
                case S.AiGotoPage:
                    if (value >= 1) GotoPage(value - 1);
                    break;
            }
        }

        // ---- navigation -----------------------------------------------------------------------------
        public int PageCount
        {
            get { return _total <= 0 ? 0 : (_total + _pageSize - 1) / _pageSize; }
        }

        /// <summary>Open a top-level list at page 1. Strand only.</summary>
        public void Open(ListKind kind)
        {
            _parentKind = ListKind.None;
            Load(kind, 0);
        }

        private void Reload()
        {
            if (_kind == ListKind.None) return;
            if (_kind == ListKind.SonglistTracks) LoadSonglist(_songlistId, _songlistName, _page);
            else Load(_kind, _page);
        }

        private void GotoPage(int page)
        {
            if (_kind == ListKind.None || _total <= 0) return;
            int max = PageCount - 1;
            if (page < 0) page = 0;
            if (page > max) page = max;
            if (page == _page) return;
            if (_kind == ListKind.Queue)
            {
                LoadQueuePage(page);
                return;
            }
            _page = page;
            Publish();
        }

        private void Back()
        {
            if (_kind != ListKind.SonglistTracks || _parentKind == ListKind.None) return;
            var parent = _parentKind;
            int page = _parentPage;
            _parentKind = ListKind.None;
            Load(parent, page);
        }

        private void Load(ListKind kind, int page)
        {
            var p = Player;
            if (p == null)
            {
                Publish();
                return;
            }
            int id = BeginLoad(kind, Titles.ContainsKey(kind) ? Titles[kind] : string.Empty);
            switch (kind)
            {
                case ListKind.Liked:
                    p.Call(Services.MiyueLibrary, "GetCollectedMusic", SoapArgs.None, r => OnTrackList(id, r, page, false));
                    break;
                case ListKind.Radios:
                    p.Call(Services.MiyueLibrary, "GetCollectedRadios", SoapArgs.None, r => OnTrackList(id, r, page, true));
                    break;
                case ListKind.Songlists:
                    p.Call(Services.MiyueLibrary, "GetCollectedSonglists", SoapArgs.None, r => OnSonglists(id, r, "Songlists", page));
                    break;
                case ListKind.Boards:
                    p.Call(Services.MiyueLibrary, "GetCollectedBoards", SoapArgs.None, r => OnSonglists(id, r, "Boards", page));
                    break;
                case ListKind.Scenes:
                    p.Call(Services.MiyueSensor, "ListSensors", SoapArgs.None, r =>
                    {
                        if (id != _loadId) return;
                        if (!r.Ok)
                        {
                            Fail(r);
                            return;
                        }
                        var list = new List<BrowseEntry>();
                        foreach (var s in Scene.ParseOpen(r.Get("Sensors")))
                            list.Add(new BrowseEntry { Text = s.Name, Sub = s.Cmd, Id = s.Id });
                        Loaded(list, string.Empty, page);
                    });
                    break;
                case ListKind.Queue:
                    LoadQueuePage(page);
                    break;
                default:
                    _busy = false;
                    Publish();
                    break;
            }
        }

        private int BeginLoad(ListKind kind, string title)
        {
            _loadId++;
            _kind = kind;
            _title = title;
            _busy = true;
            _status = "加载中 / Loading ...";
            Publish();
            return _loadId;
        }

        private void OnTrackList(int id, SoapResult r, int page, bool radios)
        {
            if (id != _loadId) return;
            if (!r.Ok)
            {
                Fail(r);
                return;
            }
            string didl = r.Get("Result");
            // old collect paths stored radios without a stream URL; Android never self-resolves them
            if (radios) didl = DidlDoc.FillRadioRes(didl);
            Loaded(TrackEntries(DidlDoc.Parse(didl)), didl, page);
        }

        private void OnSonglists(int id, SoapResult r, string outArg, int page)
        {
            if (id != _loadId) return;
            if (!r.Ok)
            {
                Fail(r);
                return;
            }
            var list = new List<BrowseEntry>();
            foreach (var s in Songlist.Parse(r.Get(outArg)))
            {
                list.Add(new BrowseEntry
                {
                    Text = s.Name,
                    Sub = s.Count > 0 ? s.Count.ToString(CultureInfo.InvariantCulture) + " 首" : string.Empty,
                    Icon = s.Icon,
                    Id = s.Id,
                });
            }
            Loaded(list, string.Empty, page);
        }

        private void LoadSonglist(string songlistId, string name, int page)
        {
            var p = Player;
            if (p == null)
            {
                Publish();
                return;
            }
            _songlistId = songlistId;
            _songlistName = name;
            int id = BeginLoad(ListKind.SonglistTracks, name);
            p.Call(Services.MiyueLibrary, "GetSonglistTracks", SoapArgs.Of("SonglistId", songlistId), r => OnTrackList(id, r, page, false));
        }

        private void LoadQueuePage(int page)
        {
            var p = Player;
            if (p == null)
            {
                Publish();
                return;
            }
            int id = BeginLoad(ListKind.Queue, Titles[ListKind.Queue]);
            if (page < 0) page = 0;
            var args = new SoapArgs().Add("StartIndex", page * _pageSize).Add("RequestedCount", _pageSize);
            p.Call(Services.MiyueQueue, "GetQueue", args, r =>
            {
                if (id != _loadId) return;
                if (!r.Ok)
                {
                    Fail(r);
                    return;
                }
                _total = Math.Max(0, r.GetInt("Total", 0));
                _queueCurrent = r.GetInt("CurrentIndex", -1);
                _entries = TrackEntries(DidlDoc.Parse(r.Get("Result")));
                if (_total < _entries.Count) _total = _entries.Count;
                int maxPage = Math.Max(0, PageCount - 1);
                if (page > maxPage && _total > 0)
                {
                    LoadQueuePage(maxPage); // the queue shrank under us
                    return;
                }
                _page = page;
                _didl = string.Empty;
                _busy = false;
                _status = string.Empty;
                Publish();
            });
        }

        private static List<BrowseEntry> TrackEntries(List<DidlItem> items)
        {
            var list = new List<BrowseEntry>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                var t = items[i];
                list.Add(new BrowseEntry
                {
                    Text = t.Title.Length > 0 ? t.Title : "Track " + (i + 1),
                    Sub = t.Artist,
                    Icon = t.AlbumArt,
                    Id = t.Id,
                    Track = t,
                });
            }
            return list;
        }

        private void Loaded(List<BrowseEntry> entries, string didl, int page)
        {
            _entries = entries;
            _didl = didl ?? string.Empty;
            _total = entries.Count;
            _queueCurrent = -1;
            int max = Math.Max(0, PageCount - 1);
            _page = Math.Max(0, Math.Min(page, max));
            _busy = false;
            _status = entries.Count == 0 ? "空列表 / Empty" : string.Empty;
            Publish();
        }

        private void Fail(SoapResult r)
        {
            _busy = false;
            _entries = new List<BrowseEntry>();
            _total = 0;
            _page = 0;
            _status = "读取失败 / Load failed: " + r;
            Publish();
        }

        // ---- actions --------------------------------------------------------------------------------
        /// <summary>Act on slot n (1-based) of the current page. Strand only.</summary>
        public void Select(int slot)
        {
            if (_busy || slot < 1 || slot > _pageSize) return;
            int abs = _page * _pageSize + (slot - 1);
            var p = Player;
            if (p == null) return;
            switch (_kind)
            {
                case ListKind.Queue:
                    if (abs < _total) p.SeekToTrack(abs);
                    break;
                case ListKind.Liked:
                case ListKind.Radios:
                case ListKind.SonglistTracks:
                    if (abs < _entries.Count) PlayTrackList(p, abs);
                    break;
                case ListKind.Songlists:
                case ListKind.Boards:
                    if (abs < _entries.Count)
                    {
                        _parentKind = _kind;
                        _parentPage = _page;
                        LoadSonglist(_entries[abs].Id, _entries[abs].Text, 0);
                    }
                    break;
                case ListKind.Scenes:
                    if (abs < _entries.Count) p.ExecuteScene(_entries[abs].Id);
                    break;
            }
        }

        /// <summary>Play the whole current list from its first track (track lists) / restart the queue.</summary>
        public void PlayAll()
        {
            var p = Player;
            if (p == null || _busy) return;
            switch (_kind)
            {
                case ListKind.Queue:
                    if (_total > 0) p.SeekToTrack(0);
                    break;
                case ListKind.Liked:
                case ListKind.Radios:
                case ListKind.SonglistTracks:
                    if (_entries.Count > 0) PlayTrackList(p, 0);
                    break;
            }
        }

        private void PlayTrackList(PlayerEngine p, int start)
        {
            var t = _entries[start].Track;
            if (t != null && DidlDoc.IsDirectLinkSrc(t.SongSrc) && t.Res.Length == 0)
            {
                _status = "「" + t.Title + "」缺少直链,请在 App 里重新收藏 / missing stream URL (songSrc=" + t.SongSrc + "), re-collect it in the app";
                Publish();
                return;
            }
            string didl = _didl.Length > 0 ? _didl : DidlDoc.Wrap(RawsOf(_entries));
            p.ReplaceQueue(didl, start, r =>
            {
                _status = r.Ok ? string.Empty : "播放失败 / Play failed: " + r;
                Publish();
            });
        }

        private static List<string> RawsOf(List<BrowseEntry> entries)
        {
            var raws = new List<string>();
            foreach (var e in entries)
                if (e.Track != null && e.Track.Raw.Length > 0) raws.Add(e.Track.Raw);
            return raws;
        }

        // ---- outputs --------------------------------------------------------------------------------
        private void Publish()
        {
            var o = _out;
            o.Digital(S.DoPlayerFound, _player != null);
            o.Digital(S.DoBusy, _busy);
            o.Digital(S.DoCanBack, _kind == ListKind.SonglistTracks && _parentKind != ListKind.None);
            int pages = PageCount;
            o.Digital(S.DoHasPrev, _page > 0);
            o.Digital(S.DoHasNext, _page + 1 < pages);
            o.Analog(S.AoPage, pages > 0 ? _page + 1 : 0);
            o.Analog(S.AoPageCount, pages);
            o.Analog(S.AoTotalItems, _total);
            o.Serial(S.SoListTitle, _title);
            o.Serial(S.SoStatus, _status);

            int onPage = 0;
            for (int slot = 1; slot <= S.MaxPageSize; slot++)
            {
                BrowseEntry e = null;
                bool current = false;
                if (slot <= _pageSize)
                {
                    if (_kind == ListKind.Queue)
                    {
                        int local = slot - 1; // queue entries hold only the current page
                        if (local < _entries.Count) e = _entries[local];
                        current = e != null && _queueCurrent == _page * _pageSize + local;
                    }
                    else
                    {
                        int abs = _page * _pageSize + slot - 1;
                        if (abs < _entries.Count) e = _entries[abs];
                    }
                }
                if (e != null) onPage++;
                o.Serial((ushort)(S.SoItemTextBase + slot), e != null ? e.Text : string.Empty);
                o.Serial((ushort)(S.SoItemSubBase + slot), e != null ? e.Sub : string.Empty);
                o.Serial((ushort)(S.SoItemIconBase + slot), e != null ? e.Icon : string.Empty);
                o.Digital((ushort)(S.DoItemCurrentBase + slot), current);
            }
            o.Analog(S.AoItemsOnPage, onPage);
        }
    }
}
