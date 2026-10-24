<p align="center">
<a href="https://github.com/hcjike/AdbTools/releases"><img alt="releases" src="https://img.shields.io/github/release/hcjike/AdbTools.svg?style=flat-square"/></a>
<a href="https://github.com/hcjike/AdbTools/blob/master/LICENSE"><img alt="license" src="https://img.shields.io/github/license/hcjike/AdbTools?style=flat-square"/></a>
<a href="https://github.com/hcjike/AdbTools/releases"><img alt="downloads" src="https://img.shields.io/github/downloads/hcjike/AdbTools/total.svg?style=flat-square"/></a>
<a href="https://github.com/hcjike/AdbTools/commits"><img alt="commits" src="https://img.shields.io/github/last-commit/hcjike/AdbTools.svg?style=flat-square"/></a>
</p>


### 开发环境
> 使用VS2022开发
> 
> 依赖 [.NET4.6下载](https://www.microsoft.com/zh-cn/download/details.aspx?id=48137)

### 运行环境
- 需要`.NET4.6`运行时环境；
- `64位Windows`电脑运行。

### 连接准备
[观看视频](/docs/videos/1.mp4)
#### 开启开发者模式
- `设置 - 关于 - 版本号`，连续点击`7次`开启开发者模式；
#### 安卓11及以上
- `设置 - 系统 - 开发者选项`，找到`无线调试`功能，`连接WiFi`后点击`开启`，进入`无线调试`，查看`IP地址和端口`，使用此地址即可无线连接到设备。
- **注：部分设备位置不同，需要自行寻找**

#### 安卓11以下
- `连接WiFi`查看当前设备的`局域网IP地址`，使用此地址即可无线连接到设备，此时无需输入端口号。
- **注：过低版本的设备可能无法连接**

### 安装和使用
- 解压到`不含空格和中文`的目录；
- 运行`AdbTools.exe`程序，开始使用。

### 设备配对
- `IP和端口`输入框中输入安卓设备`使用配对码配对设备`提供的`IP:Port`地址；
- `配对码`输入框中输入`配对码`，点击`配对设备`进行配对。

### 设备连接
- `IP和端口`输入框中输入安卓设备`无线连接`的`IP:Port`地址，支持省略`Port`只输入`IP`，此时端口默认为`5555`；
- 点击`连接设备`，等待连接成功。

### 历史记录
- 连接成功的设备会在历史纪录中展示；
- `IP和端口`右侧打开下拉选，右键下拉选中的内容，点击菜单进行操作。

### 安装apk
- 右键`已连接列表`中的设备`IP:Port`形式的地址，点击`安装应用`选择需要安装的`apk`程序，等待完成安装。


