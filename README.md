# UEFN-AES-Loader-Assistant

**UEFN-AES-Loader-Assistant**は、[UEFN-AES-Loader](https://github.com/Aleman-sein-Vater/UEFN-AES-Loader) のセットアップを簡単にするためのアシストツールです。

このツール自体にはAESの復号機能は含まれておらず、[Deutsche Alman](https://github.com/Aleman-sein-Vater) 氏が公開している **UEFN-AES-Loader** を利用しやすくすることを目的としています。

## 必要なもの

* [PIE](https://discord.gg/zjurusqhpD)
* [UEFN-AES-Loader](https://github.com/Aleman-sein-Vater/UEFN-AES-Loader)

## セットアップ

1. [UEFN-AES-Loader](https://github.com/Aleman-sein-Vater/UEFN-AES-Loader) のリポジトリから `UEFNContentKey.dll`をダウンロードします。
2. ダウンロード後dllの名前を`amfrt64.dll`に変更します。
3. ダウンロードしたDLLを `FortniteGame/Binaries/Win64` フォルダへ配置します。
4. 一度PIEを起動し、その後終了します。

## 使用方法

1. **UEFN-AES-Loader-Assistant**のexeを起動します。
2. 読み込みたいマップのマップコード(例:1234-5678-9012)を入力します。
3. 処理が完了するまで待ちます。
4. 再度 UEFN で **PIE** を起動します。

## 動作

処理完了後にPIEを起動すると、対象のマップが自動でPIEにロードされます。
また、マップデータが配置されているコンテンツドロワーの場所が自動で開かれるため、アセットへすぐにアクセスできます。

## Credit:
[FNJPNews](https://github.com/FNJPNews)さんの[UEFNDownloader](https://github.com/FNJPNews/UEFNDownloader)をこのツールのコアとして使っています。
