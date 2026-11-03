using System;

namespace AdbTools.RemoteControl
{
    /// <summary>
    /// 视频取流后端统一接口。实现可以是设备端 MediaCodec 主动编码（scrcpy server），
    /// 也可以是 adb screenrecord 直出；上层解码、显示与降档逻辑不依赖具体后端。
    /// </summary>
    internal interface IVideoStream : IDisposable
    {
        /// <summary>请求的采集尺寸，形如 1280x720；运行中修改后调用 Restart 生效。</summary>
        string Size { get; set; }

        /// <summary>码率（bps），下一次片段生效。</summary>
        int BitRate { get; set; }

        /// <summary>已启动的取流片段数（诊断用）。</summary>
        int SegmentCount { get; }

        /// <summary>设备息屏期间暂停取流，避免无意义的重连。</summary>
        bool Paused { get; set; }

        /// <summary>暂停看门狗的“无数据”判定。</summary>
        bool SuppressWatchdog { get; set; }

        /// <summary>日志回调（在取流线程中触发）。</summary>
        event Action<string> Log;

        /// <summary>取流前唤醒、解锁并保持常亮。</summary>
        void WakeUpDevice();

        /// <summary>窗口关闭时清理设备端残留进程与 adb 转发。</summary>
        void ReleaseDevice();

        /// <summary>阻塞读取，直到调用 Stop；片段结束后自动重启。</summary>
        void Pump(Action<byte[], int> onData);

        /// <summary>强制结束当前片段，用新参数重建取流。</summary>
        void Restart();

        /// <summary>停止取流（幂等）。</summary>
        void Stop();
    }
}
