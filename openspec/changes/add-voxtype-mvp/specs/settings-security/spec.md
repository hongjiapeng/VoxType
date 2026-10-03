# Spec Delta

## Purpose

让用户以统一设置界面配置第一版语音输入工具，保存有效服务与快捷键选择，并保护识别凭据和本地录音。定义配置何时生效以及存储损坏时可恢复的行为。

## ADDED Requirements

### Requirement: 三个设置页面
设置 SHALL 仅实现常规、语音识别、快捷键三个导航入口，使用 Windows Fluent 交互和 MVVM；未实现的历史、模型市场等功能不显示为可用页面。

#### Scenario: 打开设置
- **WHEN** 用户从托盘打开设置
- **THEN** 能访问常规、语音识别、快捷键及对应真实配置

### Requirement: Provider 驱动的统一表单
语音识别设置 SHALL 在同一页面选择 Whisper Local 或 OpenAI Compatible，按服务动态显示字段和本地或上传说明；本地显示固定 base 模型安装管理、手动模型路径、语言，在线显示 Base URL、API Key、Model、Language。

#### Scenario: 切换服务
- **WHEN** 用户选择 OpenAI Compatible
- **THEN** 表单显示在线字段和上传提示，保存后下一次会话使用该服务

### Requirement: 有效配置持久化
应用 SHALL 保存通过校验的配置，并在下次启动恢复；保存失败时保留当前有效配置，提供错误；损坏配置不得导致应用崩溃或静默启用在线模式。

#### Scenario: 保存后重启
- **WHEN** 用户保存服务、模型、语言与快捷键并重启
- **THEN** 有效配置恢复，默认隐藏设置并常驻托盘

#### Scenario: 配置损坏
- **WHEN** 本地配置文件无效
- **THEN** 使用安全的本地默认值，提示重新配置，不上传音频

### Requirement: 凭据受当前用户保护
应用 SHALL 使用 Windows 当前用户保护的加密凭据持久化 API Key，普通配置和日志不包含明文 Key；解密失败时提示重新输入，不静默发送错误凭据。

#### Scenario: 检查存储
- **WHEN** 用户保存 API Key
- **THEN** 配置磁盘文件中找不到明文 Key，界面以密码字段显示

#### Scenario: 无法解密
- **WHEN** 加密凭据无法在当前 Windows 用户下解密
- **THEN** 清晰提示重输 Key，保留其他有效配置，不把解密错误吞掉

### Requirement: 端点和隐私边界
应用 SHALL 默认本地模式，不持久化录音或转录历史；在线模式只向配置端点发送本次音频，API Key 不经重定向发送至另一主机。本地服务失败时不得自动上传。

#### Scenario: 本地断网运行
- **WHEN** 用户配置有效模型并断开网络
- **THEN** 本地识别继续工作，没有音频网络上传或历史文件

#### Scenario: 非本机明文端点
- **WHEN** 用户输入非 loopback 的 HTTP 地址
- **THEN** 保存被拒绝并提示使用 HTTPS；localhost、127.0.0.1 和 ::1 可用 HTTP

#### Scenario: 重定向端点
- **WHEN** 配置服务返回 HTTP 重定向
- **THEN** 不自动携带 Key 跟随重定向，提示检查 Base URL

### Requirement: 有限待复制结果
应用 SHALL 只在内存中保留最近一次需手动处理的结果，提供可见复制入口，后续结果替换旧结果；退出后不恢复该结果，不建立历史管理。

#### Scenario: 复制失败后的找回
- **WHEN** 用户收到剪贴板写入失败提示并打开常规设置
- **THEN** 可以查看并复制待发送文本；应用退出后该文本不写入磁盘
