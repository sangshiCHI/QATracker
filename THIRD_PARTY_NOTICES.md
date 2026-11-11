# 第三方与平台声明

QuestAway Companion 原创诊断代码沿用上级项目 MIT 许可；QuestAway 名称及任何游戏内容属于各自权利人。分发包不含游戏程序、美术、存档或账号数据。

## Microsoft Windows.UI.Composition-Win32-Samples

来源：https://github.com/microsoft/Windows.UI.Composition-Win32-Samples

本工程参考官方 WGC/D3D11 WinRT 互操作模式并以当前 .NET 平台编写精简桥接，不分发原示例工程、SharpDX 或其旧依赖。保留官方 MIT 声明：

MIT License

Copyright (c) Microsoft Corporation. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## 微软平台与自包含运行时

Windows API、Windows Graphics Capture、Windows OCR 由操作系统提供。Microsoft.Windows.SDK.NET.Ref 来自微软官方 NuGet，版本/SDK 条款以包内元数据和 [Windows SDK 许可](https://aka.ms/WinSDKLicenseURL)为准，不将整个 SDK 重新打包。

自包含发布包包含 .NET、WPF/WindowsDesktop 与 Windows SDK .NET 投影运行组件，适用各自许可，不统一改称本项目 MIT。构建脚本随包复制官方 .NET 运行时 LICENSE/THIRD-PARTY-NOTICES、WPF LICENSE、CsWinRT MIT 许可至 licenses 子目录，并附本机安装的 Windows SDK 19041 许可/第三方声明供核对。具体 NuGet 投影的分发权利仍须遵守上述包内元数据指向的 Windows SDK 许可，公开发布前复核。

## Microsoft.Data.Sqlite 与 SQLitePCLRaw（已集成）

Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core 10.0.11：MIT，Copyright (c) .NET Foundation and Contributors，原文附于 licenses/Microsoft.Data.Sqlite-LICENSE.txt。

SQLitePCLRaw.bundle_e_sqlite3、core、provider.e_sqlite3、lib.e_sqlite3 2.1.12：Apache-2.0，Copyright 2014-2024 SourceGear, LLC；许可原文附于 licenses/SQLitePCLRaw-APACHE-2.0.txt。本机 NuGet 恢复未报告漏洞警告，不等于所有原生组件或未来版本不存在漏洞。SQLite 本体的[公共领域说明](https://www.sqlite.org/copyright.html)及上层分发包条款分别适用。

仅用微软 provider 和参数化 SQLite，不引入 Entity Framework ORM、远程数据库或下载执行安装脚本。SQLite统计记录保存在本机，不随匿名在线信号或版本检查上传；原生库随自包含包提供。

## 仅调查、未集成

- Text Grab：https://github.com/TheJoeFin/Text-Grab，已检查根 MIT，Copyright (c) 2020 Joseph Finney；本包不包含其源码、应用、安装程序、图标或依赖。
- Tesseract：https://github.com/tesseract-ocr/tesseract，Apache-2.0；本轮没有分发引擎、Windows 包装器或语言模型。

候选项目的许可证不自动覆盖其所有依赖；后续实际引入时重新核查具体版本并补齐 notices。

## 中文离线参考资料
资料来源 https://questaway.wiki/ 的公开索引、中文页面及游戏1.0.9名称和规则资料，整理及文案复核日期为2026-11-11。说明性长文、技能故事及区域描述已重新撰写；保留查询需要的名称、短条件、配方、数值、公式和表格结构，不分发站点程序、广告、统计脚本、玩家存档或登录资料。原站工具的历史说明不代表本助手具有那些在线功能或已通过同等测试。

本资料不是官方指南，不代表获得游戏或原站的整体授权。本项目MIT许可证仅适用于本项目原创代码，不覆盖QuestAway名称及第三方内容；引用来源不等于第三方作品改为MIT。未取得整站原文再分发许可，因此不把先前未经改写的全文快照、原游戏图片或私有研究资料纳入本测试发行物。动态服务、市场报价、网页计算器及后续游戏版本不保证离线可用。

## 当前替代图标与插图

当前图片候选使用 Game-icons.net 图标库（固定提交82d948812bfe3f269ef8f731dcdb07b08160edc4），CC BY 3.0。图形由原作者创作；本项目添加棕金边框、着色、尺寸适配及等级角标。85份源SVG覆盖1857个原图片引用及10个职业图标，部分为同类示意图，并非游戏原画。

Icons made by Lorc, Delapouite, Caro Asercion, Cathelineau, DarkZaitzev, Irongamer and Skoll. 来源 https://game-icons.net/ 和 https://github.com/game-icons/icons/tree/82d948812bfe3f269ef8f731dcdb07b08160edc4 。许可 https://creativecommons.org/licenses/by/3.0/ 。逐图来源及改动记录在data/licensed-art/attribution.json和replacement-map.json；原作者与许可说明见licenses/Game-icons-LICENSE.txt和Game-icons-CREDITS.txt；玩家包另附licenses/Game-icons-attribution.json。CC BY许可要求在再分发时保留署名、来源、许可及改动说明；本项目MIT不替代该许可。

此前本机候选所含游戏图片及头像已移除，原件仅留本机恢复副本。替代图标具有上述单独许可，不能据此推定获得游戏原画、原站全文或其他内容的再分发授权。
