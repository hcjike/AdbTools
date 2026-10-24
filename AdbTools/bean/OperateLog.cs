using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AdbTools.bean
{
    public class OperateLog : INotifyPropertyChanged
    {
        private string _Message;
        public string Message
        {
            get => _Message;
            set => SetProperty(ref _Message, value);
        }

        private string _Status;
        public string Status
        {
            get => _Status;
            set => SetProperty(ref _Status, value);
        }

        private Brush _StatusColor;
        public Brush StatusColor
        {
            get => _StatusColor;
            set => SetProperty(ref _StatusColor, value);
        }

        public string Cmd { get; set; }


        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 设置属性值，值变化时通知界面（属性名自动获取）
        /// </summary>
        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
