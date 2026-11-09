using Microsoft.VisualBasic.CompilerServices;
using PCL.Core.App;
using PCL.Core.Utils;
using System.Collections;
using System.IO;
using System.Windows.Shell;
using PCL.Network;
using PCL.Network.Loaders;

using PCL.Core.App.Localization;
namespace PCL;

public static partial class ModLoader
{

    /// <summary>
    ///     支持多个加载器连续运作的复合加载器。
    /// </summary>
    public class LoaderCombo : LoaderBase
    {
        public object? input;

        public List<LoaderBase> loaders = new();

        public LoaderCombo(string name, IEnumerable<LoaderBase> loaders)
        {
            this.loaders.Clear();
            foreach (var Loader in loaders)
                if (Loader is not null)
                {
                    this.loaders.Add(Loader);
                    Loader.OnStateChangedThread += SubTaskStateChanged;
                    Loader.hasOnStateChangedThread = true;
                }

            InitParent(null);
            this.name = name;
        }

        public override double Progress
        {
            get
            {
                switch (State)
                {
                    case ModBase.LoadState.Waiting:
                    {
                        return 0d;
                    }
                    case ModBase.LoadState.Loading:
                    {
                        var total = 0d;
                        var finished = 0d;
                        foreach (var Loader in loaders)
                        {
                            total += Loader.ProgressWeight;
                            finished += Loader.ProgressWeight * Loader.Progress;
                        }

                        if (total == 0d)
                            return 0d;
                        return finished / total;
                    }

                    default:
                    {
                        return 1d;
                    }
                }
            }
            set => throw new Exception("多重加载器不支持设置进度");
        }

        public override void InitParent(LoaderBase parent)
        {
            this.parent = parent;
            foreach (var Loader in loaders)
                Loader.InitParent(this);
        }

        public override void Start(object input = null, bool isForceRestart = false)
        {
            isForceRestarting = isForceRestart;
            lock (lockState)
            {
                if (State == ModBase.LoadState.Loading) return;

                State = ModBase.LoadState.Loading;
            }

            // 启动加载
            this.input = input;
            if (isForceRestart)
                foreach (var Loader in loaders)
                    Loader.State = ModBase.LoadState.Waiting;
            ModBase.RunInThread(Update);
        }

        public override void Abort()
        {
            lock (lockState)
            {
                if (State != ModBase.LoadState.Loading && State != ModBase.LoadState.Waiting)
                    return;
            }

            foreach (var Loader in loaders) Loader.Abort();

            lock (lockState)
            {
                if (State == ModBase.LoadState.Loading || State == ModBase.LoadState.Waiting)
                    State = ModBase.LoadState.Aborted;
            }
        }

        /// <summary>
        ///     子任务状态变更。
        /// </summary>
        private void SubTaskStateChanged(LoaderBase loader, ModBase.LoadState newState, ModBase.LoadState oldState)
        {
            switch (newState)
            {
                case ModBase.LoadState.Loading:
                {
                    break;
                }
                // 开始，啥都不干
                case ModBase.LoadState.Waiting:
                {
                    break;
                }
                // 子加载器可能由于外部输入改变而暂时变为 Waiting，之后会立即重新启动
                // 所以啥都不干就行
                case ModBase.LoadState.Finished:
                {
                    // 正常结束，触发刷新
                    Update();
                    break;
                }
                case ModBase.LoadState.Aborted:
                {
                    // 被中断，这个任务也中断
                    Abort();
                    break;
                }

                default:
                {
                    // 完蛋，出错了
                    lock (lockState)
                    {
                        if (State >= ModBase.LoadState.Finished)
                            return;
                        Error = new Exception(loader.name + "失败", loader.Error);
                        State = loader.State;
                    }

                    foreach (var currentLoader in loaders)
                    {
                        loader = currentLoader;
                        loader.Abort();
                    }

                    ModMain.frmMain.BtnExtraDownload.ShowRefresh();
                    return;
                }
            }
        }

        /// <summary>
        ///     触发一次更新，以启动新加载器或完成。
        /// </summary>
        private void Update()
        {
            if (State == ModBase.LoadState.Finished
                || State == ModBase.LoadState.Failed
                || State == ModBase.LoadState.Aborted)
                return;

            var isFinished = true;
            var blocked = false;
            object input = this.input;

            foreach (var loader in loaders)
                switch (loader.State)
                {
                    case ModBase.LoadState.Finished:
                    {
                        if (loader is LoaderTask task)
                        {
                            var genericArg = task.GetType().GenericTypeArguments.FirstOrDefault();
                            var shouldInput = input is not null && genericArg == input.GetType()
                                ? input
                                : null;

                            if (task.ShouldStart(ref shouldInput, false, true))
                            {
                                ModBase.Log("[Loader] 由于输入条件变更，重启已完成的加载器 " + loader.name);
                                goto Restart;
                            }

                            input = ((dynamic)loader).output; // 何意味啊，没法匹配 LoaderTask<,>
                        }

                        if (loader.block && !isFinished)
                            blocked = true;

                        break;
                    }

                    case ModBase.LoadState.Loading:
                    {
                        if (loader is LoaderTask task)
                        {
                            var genericArg = loader.GetType().GenericTypeArguments.FirstOrDefault();
                            var shouldInput = input is not null && genericArg == input.GetType()
                                ? input
                                : null;
                            if (task.ShouldStart(ref shouldInput, false, true))
                            {
                                ModBase.Log($"[Loader] 由于输入条件变更，重启进行中的加载器 {loader.name}",
                                    ModBase.LogLevel.Developer);
                                goto Restart;
                            }
                        }

                        isFinished = false;
                        blocked = true;
                        break;
                    }

                    default:

                        Restart:

                        isFinished = false;

                        if (blocked)
                            continue;

                        if (input is not null)
                        {
                            switch (loader)
                            {
                                case LoaderTask:
                                case LoaderCombo:
                                    var genericArg = loader.GetType().GenericTypeArguments.FirstOrDefault();

                                    loader.Start(
                                        genericArg == input.GetType() ? input : null,
                                        isForceRestarting);
                                    break;
                                case not null:
                                    loader.Start(
                                        input is List<DownloadFile> ? input : null,
                                        isForceRestarting);
                                    break;
                                default:
                                    throw new Exception($"未知的加载器类型（{loader?.GetType()}）");
                            }
                        }
                        else
                        {
                            loader.Start(isForceRestart: isForceRestarting);
                        }

                        if (loader.block)
                            blocked = true;

                        break;
                }

            if (isFinished)
            {
                RaisePreviewFinish();
                State = ModBase.LoadState.Finished;
                ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            }
        }

        /// <summary>
        ///     获得最底层的，应被显示给用户的加载器列表，并追加于 List。
        /// </summary>
        public static void GetLoaderList(LoaderCombo loader, ref List<LoaderBase> list, bool requireShow = true)
        {
            foreach (var SubLoader in loader.loaders)
            {
                if (SubLoader.show || !requireShow)
                    list.Add(SubLoader);
                if (SubLoader is LoaderCombo combo)
                    GetLoaderList(combo, ref list);
            }
        }

        /// <summary>
        ///     获得最底层的，应被显示给用户的加载器列表，并追加于 List。
        /// </summary>
        public void GetLoaderList(ref List<LoaderBase> list, bool requireShow = true)
        {
            GetLoaderList(this, ref list, requireShow);
        }

        /// <summary>
        ///     获得最底层的，应被显示给用户的加载器列表。
        /// </summary>
        public List<LoaderBase> GetLoaderList(bool requireShow = true)
        {
            var list = new List<LoaderBase>();
            GetLoaderList(ref list, requireShow);
            return list;
        }
    }


    /// <summary>
    ///     支持多个加载器连续运作的复合加载器（泛型版本）。
    /// </summary>
    public class LoaderCombo<InputType> : LoaderCombo
    {
        public InputType typedInput;

        public LoaderCombo(string name, IEnumerable<LoaderBase> loaders) : base(name, loaders) { }

        public override void Start(object input = null, bool isForceRestart = false)
        {
            this.typedInput = Conversions.ToGenericParameter<InputType>(input);
            base.Start(this.typedInput, isForceRestart);
        }
    }


    private struct LoaderFolderDictionaryEntry
    {
        public DateTime? lastCheckTime;
        public string folderPath;

        public override bool Equals(object obj)
        {
            if (obj is not LoaderFolderDictionaryEntry)
                return false;
            var entry = (LoaderFolderDictionaryEntry)obj;
            return EqualityComparer<DateTime?>.Default.Equals(lastCheckTime, entry.lastCheckTime) &&
                   (folderPath ?? "") == (entry.folderPath ?? "");
        }

        public override int GetHashCode() => HashCode.Combine(lastCheckTime, folderPath ?? "");
    }

}
