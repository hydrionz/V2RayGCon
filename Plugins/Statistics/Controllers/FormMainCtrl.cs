﻿using Statistics.Resources.Langs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Statistics.Controllers
{
    public class FormMainCtrl
    {
        Services.Settings settings;
        VgcApis.Models.IServersService vgcServers;

        ListView dataView;
        ToolStripMenuItem miReset, miResizeByTitle, miResizeByContent;

        // const int updateInterval = 5000; // debug
        const int updateInterval = 2000;
        Timer updateDataViewTimer = new Timer
        {
            Interval = updateInterval,
        };

        bool requireReset = false;
        bool[] sortFlags = new bool[] { false, false, false, false, false };

        public FormMainCtrl(
            Services.Settings settings,
            VgcApis.Models.IServersService vgcServers,

            ListView dataView,

            ToolStripMenuItem miReset,
            ToolStripMenuItem miResizeByTitle,
            ToolStripMenuItem miResizeByContent)
        {
            this.settings = settings;
            this.vgcServers = vgcServers;

            this.dataView = dataView;

            this.miReset = miReset;
            this.miResizeByContent = miResizeByContent;
            this.miResizeByTitle = miResizeByTitle;
        }

        #region public methods
        public void Cleanup()
        {
            ReleaseUpdateTimer();
        }

        public void Run()
        {
            ResizeDataViewByTitle();
            updateDataViewTimer.Tick += UpdateDataViewHandler;
            updateDataViewTimer.Start();
            BindControlEvent();
            ShowStatsDataOnDataView();
        }
        #endregion

        #region private methods
        private void BindControlEvent()
        {
            miReset.Click += (s, a) =>
            {
                Task.Factory.StartNew(() =>
                {
                    if (VgcApis.Libs.UI.Confirm(I18N.ConfirmResetStatsData))
                    {
                        requireReset = true;
                    }
                });
            };

            miResizeByContent.Click += (s, a) => ResizeDataViewByContent();

            miResizeByTitle.Click += (s, a) => ResizeDataViewByTitle();

            dataView.ColumnClick += ColumnClickHandler;
        }

        void ColumnClickHandler(object sender, ColumnClickEventArgs args)
        {
            var index = args.Column;
            if (index < 0 || index > 4)
            {
                return;
            }

            if (index == 0)
            {
                dataViewOrderKeyIndex = 0;
            }
            else
            {
                sortFlags[index] = !sortFlags[index];
                dataViewOrderKeyIndex = index * (sortFlags[index] ? 1 : -1);
            }
            ShowStatsDataOnDataView();
        }

        void UpdateDataViewHandler(object sender, EventArgs args)
        {
            Task.Factory.StartNew(UpdateDataViewWorker);
        }

        Models.StatsResult GetterCoreInfo(VgcApis.Models.ICoreCtrl coreCtrl)
        {
            var result = new Models.StatsResult();
            result.title = coreCtrl.GetTitle();
            result.uid = coreCtrl.GetUid();

            var curData = coreCtrl.Peek();
            if (curData != null)
            {
                result.stamp = curData.stamp;
                result.totalUp = curData.statsUplink;
                result.totalDown = curData.statsDownlink;
            }
            return result;
        }

        void ReleaseUpdateTimer()
        {
            updateDataViewTimer.Stop();
            updateDataViewTimer.Tick -= UpdateDataViewHandler;
            updateDataViewTimer.Dispose();
        }

        readonly object updateDataViewLocker = new object();
        bool isUpdating = false;
        void UpdateDataViewWorker()
        {
            lock (updateDataViewLocker)
            {
                if (isUpdating)
                {
                    return;
                }
                isUpdating = true;
            }

            if (requireReset)
            {
                settings.ClearStatsData();
                requireReset = false;
            }

            UpdateHistoryStatsDatas();
            settings.SaveAllStatsData();
            ShowStatsDataOnDataView();

            lock (updateDataViewLocker)
            {
                isUpdating = false;
            }
        }

        int dataViewOrderKeyIndex = 0;
        void ShowStatsDataOnDataView()
        {
            var sortedContent = GetSortedHistoryDatas();
            var lvContent = sortedContent
                .Select(e => new ListViewItem(e))
                .ToArray();

            BatchUpdateDataView(() =>
            {
                dataView.Items.Clear();
                dataView.Items.AddRange(lvContent);
            });
        }

        IEnumerable<string[]> GetSortedHistoryDatas()
        {
            const int MiB = 1024 * 1024;
            var contents = settings.GetAllStatsData()
                .Select(d =>
                {
                    var v = d.Value;
                    return new string[] {
                        v.title,
                        v.curDownSpeed.ToString(),
                        v.curUpSpeed.ToString(),
                        (v.totalDown/MiB).ToString(),
                        (v.totalUp/MiB).ToString(),
                    };
                });

            var index = dataViewOrderKeyIndex;
            switch (Math.Sign(index))
            {
                case 1:
                    return contents.OrderBy(
                        e => VgcApis.Libs.Utils.Str2Int(e[index]));
                case -1:
                    return contents.OrderByDescending(
                        e => VgcApis.Libs.Utils.Str2Int(e[-index]));
                default:
                    return contents;
            }
        }

        void ResetCurSpeed(Dictionary<string, Models.StatsResult> datas)
        {
            foreach (var data in datas)
            {
                data.Value.curDownSpeed = 0;
                data.Value.curUpSpeed = 0;
            }
        }

        void UpdateHistoryStatsDatas()
        {
            var newDatas = vgcServers
                .GetAllServersList()
                .Where(s => s.IsCoreRunning())
                .OrderBy(s => s.GetIndex())
                .Select(s => GetterCoreInfo(s))
                .ToList();

            var historyDatas = settings.GetAllStatsData();
            ResetCurSpeed(historyDatas);

            foreach (var d in newDatas)
            {
                var uid = d.uid;
                if (!historyDatas.ContainsKey(uid))
                {
                    historyDatas[uid] = d;
                    return;
                }
                MergeNewDataIntoHistoryDatas(historyDatas, d, uid);
            }
        }

        private static void MergeNewDataIntoHistoryDatas(
            Dictionary<string, Models.StatsResult> datas,
            Models.StatsResult statsResult,
            string uid)
        {
            var p = datas[uid];

            var elapse = 1.0 * (statsResult.stamp - p.stamp) / TimeSpan.TicksPerSecond;
            if (elapse <= 1)
            {
                elapse = updateInterval / 1000.0;
            }

            var downSpeed = (statsResult.totalDown / elapse) / 1024.0;
            var upSpeed = (statsResult.totalUp / elapse) / 1024.0;
            p.curDownSpeed = Math.Max(0, (int)downSpeed);
            p.curUpSpeed = Math.Max(0, (int)upSpeed);
            p.stamp = statsResult.stamp;
            p.totalDown = p.totalDown + statsResult.totalDown;
            p.totalUp = p.totalUp + statsResult.totalUp;
        }

        void BatchUpdateDataView(Action action)
        {
            dataView.Invoke((MethodInvoker)delegate
            {
                dataView.BeginUpdate();
                try
                {
                    action?.Invoke();
                }
                catch { }
                finally
                {
                    dataView.EndUpdate();
                }
            });
        }

        void ResizeDataViewColumn(bool byTitle)
        {
            var mode = byTitle ?
                ColumnHeaderAutoResizeStyle.HeaderSize :
                ColumnHeaderAutoResizeStyle.ColumnContent;

            var count = dataView.Columns.Count;
            for (int i = 1; i < count; i++)
            {
                dataView.Columns[i].AutoResize(mode);
            }
        }

        void ResizeDataViewByContent()
        {
            BatchUpdateDataView(
                () => ResizeDataViewColumn(false));
        }

        void ResizeDataViewByTitle()
        {
            BatchUpdateDataView(
                () => ResizeDataViewColumn(true));
        }

        #endregion
    }
}