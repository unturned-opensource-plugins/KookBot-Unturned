# KookBot-Unturned

一个为Unturned服务器集成Kook（开黑啦）机器人功能的插件。

## 功能特性

- **聊天调制**：强大的聊天内容审核系统
  - 禁言管理（临时/永久）
  - 违禁词过滤
  - 脏话检测和屏蔽
  - 刷屏检测（消息频率、重复字符、相似消息）
  - 递增惩罚系统

- **Kook集成**：与Kook平台无缝集成
  - WebSocket连接管理
  - 卡片消息支持
  - 服务器指标监控

- **配置管理**：灵活的配置系统
  - 热重载配置
  - 自动更新检查

## 系统要求

- .NET Framework 4.8+
- Rocket.Unturned 插件框架

## 安装

1. 将编译后的DLL放入Unturned服务器的Plugins目录
2. 配置`KookBot-Unturned.configuration.xml`文件
3. 重启服务器

## 配置

编辑`KookBot-Unturned.configuration.xml`配置文件以自定义：
- 聊天审核规则
- Kook机器人令牌
- 服务器监控设置

## 开发

### 构建项目

```bash
dotnet build
```

### 项目结构

- `ChatModerationManager.cs` - 聊天审核核心逻辑
- `KookApi/` - Kook API集成
- `Monitoring/` - 服务器监控
- `Commands.cs` - 插件命令
- `Events.cs` - 事件处理

## 许可证

详见 LICENSE.txt 文件

## 作者

Emqo
