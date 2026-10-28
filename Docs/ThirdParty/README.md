# 二维码解码库

运行依赖：**ZXingCpp 0.5.3**，包内原生引擎 zxing-cpp 3.1.1，Apache-2.0。

- [官方项目](https://github.com/zxing-cpp/zxing-cpp)
- [NuGet 包及发布说明](https://www.nuget.org/packages/ZXingCpp/0.5.3)
- [完整许可证](ZXingCpp-LICENSE.txt)
- 版权归 ZXing、zxing-cpp 各原作者及贡献者；包装器作者 Axel Waggershauser。
- 下载包 SHA256：`8E5E837BD8B12BDFC77210C2DB4DE35AA5E9C8D27DCBBEE8DC2E41AE37A98FFC`。

二维码像素直接在本机内存中解码，无网络识别服务。Windows x64 的原生库由 NuGet 运行时资产随构建输出复制。

回归测试另用 ZXing.Net 0.16.11（Apache-2.0，[官方项目](https://github.com/micjahn/ZXing.Net)）生成已知内容的测试码；该包不是应用程序的运行依赖。
