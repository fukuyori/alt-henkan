# Alt Henkan

変更履歴は [CHANGELOG.md](CHANGELOG.md) を参照してください。

Alt Henkan は、左右の Alt キーを単独で押したときに追加の役割を与える、Windows の通知領域(システムトレイ)常駐アプリです。

- 左 Alt を単押し: IME をオフにする(`VK_IME_OFF`)
- 右 Alt を単押し: IME をオンにする(`VK_IME_ON`)
- Alt を単独で長押しして離す: その側で有効にしていれば、Windows 本来の Alt 単独操作(メニューバーのフォーカスなど)を実行する
- Alt を押したまま他のキーを押す、またはマウスのボタン・ホイールを使う: 通常の Alt との組み合わせをそのまま通す

長押し動作は左右の Alt で個別に有効・無効を切り替えられます。しきい値の既定値は 600 ミリ秒で、設定画面から変更できます。

IME のオン・オフは仮想キー `VK_IME_ON` / `VK_IME_OFF` として送信します。物理キーボードが US 配列でも動作し、IME 側のキー割り当て設定にも依存しません。これらの仮想キーは Windows 10 バージョン 1903 以降の Microsoft IME で対応しています。

## 動作環境

- Windows 10 または Windows 11
- .NET 8 Desktop Runtime(ソースからビルドして実行する場合。インストーラー版は自己完結型のため不要)

## 管理者権限のウィンドウについて

Windows の UIPI により、通常権限のプロセスは、管理者権限で動作しているウィンドウ宛てのキー入力を見ることも、そこへ入力を注入することもできません。そのため、通常ビルドの Alt Henkan は管理者権限のアプリが前面にある間は何もしません。リリースビルドでは `uiAccess="true"` のマニフェスト(`src/AltHenkan/app.uiaccess.manifest`)を埋め込み、Alt Henkan 自体を管理者権限で動かすことなくこの制限を解除します。Windows がこのマニフェストを有効にするのは、実行ファイルが「マシンで信頼された証明書でコード署名されている」かつ「Program Files 配下にインストールされている」場合に限られます。未署名の uiAccess 実行ファイルや、他のフォルダーから起動したものは起動を拒否されます。

`dotnet build` と `dotnet run` では通常の `app.manifest` が埋め込まれるため、開発ビルドは従来どおり `bin\` から起動できますが、UIPI の制限を受けます。

## インストール

`AltHenkan_Setup_<version>.exe` を実行してください。インストーラーはマシン単位(Program Files 配下にインストール、管理者権限が必要。上記を参照)で、次のオプションがあります。

- サインイン時に自動起動する(`HKCU\...\Run` に値 `AltHenkan` を登録。アンインストール時に削除)
- デスクトップにショートカットを作成する(既定ではオフ)

`-NoUiAccess` で作成したインストーラーはユーザー単位(管理者権限不要、`%LOCALAPPDATA%\Programs` 配下)になり、管理者権限のウィンドウでは動作しません。

## 使い方

メインウィンドウはありません。通知領域のアイコンをダブルクリックするか、右クリックして `設定...` を選ぶと設定を変更できます。

設定は `%LOCALAPPDATA%\AltHenkan\settings.json` に保存されます。

### 統合設定とEmacsモード

設定画面は `Alt設定`・`Emacs設定`・`表示設定` の3タブです。既存のAlt長押し設定と判定時間を引き継ぎます。

`Emacs設定` の「CapsLockによるEmacsモード切り替えを有効にする」をONにすると、CapsLockで通常モード／Emacsモードを切り替えます。機能は初期状態ではOFFです。起動時のモードは常に通常で、選択中のモードは保存しません。機能をOFFにすると通常モードに戻り、CapsLock本来の大文字固定を使えます。機能を有効にした際は、大文字固定がONなら解除します。

Emacsモードでも文字・数字・記号の入力は通常どおりです。次の13種類の移動・削除ショートカットのみ変換し、それぞれを個別にON／OFFできます（各操作の既定値はON）。

| キー | 操作（送信するキー） |
|---|---|
| Ctrl+B / Ctrl+F | 左 / 右 |
| Ctrl+P / Ctrl+N | 上 / 下 |
| Ctrl+A / Ctrl+E | Home / End（行頭 / 行末） |
| Alt+B / Alt+F | Ctrl+左 / Ctrl+右（前 / 次の単語） |
| Alt+< / Alt+> | Ctrl+Home / Ctrl+End（文書の先頭 / 末尾） |
| Ctrl+D | Delete（カーソル位置の文字を削除） |
| Alt+D | Ctrl+Delete（次の単語を削除） |
| Ctrl+K | Shift+End → Delete（行末まで削除。行末では改行を削除して次の行と結合） |

実際の移動・削除範囲は対象アプリの矢印・Home・End・Ctrl+矢印・Deleteなどの動作に従います。ONの操作は対象アプリ本来のショートカットより優先します。たとえばCtrl+Aの全選択、Ctrl+Pの印刷、Ctrl+Fの検索は該当項目をOFFにすると使えます。Ctrl+Vなど上記以外のショートカットは変換しません。viモードは含みません。

US配列のAlt+<はAlt+Shift+`,`、Alt+>はAlt+Shift+`.`です。この2種類はShiftを必要としますが、その他の操作にShiftを追加した組み合わせや、Ctrl+Alt・Windowsキーを追加した組み合わせは変換しません。

`Emacs設定` の「Ctrlの対象」「Altの対象」で、それぞれ「左」「右」「両方」を選べます。モード全体で共通の設定で、既定値は「両方」です。「両方」は左右どちらのキーでも使える意味で、同時押しは不要です。「左」「右」は指定した側が押されているときに変換し、対象外の側だけで押した操作は通常どおり通します。この指定はAlt単独のIMEオン／オフや長押し設定には影響しません。旧バージョンの設定では左右とも「両方」となり、既存の個別ON／OFF設定を引き継ぎます。

削除操作は切り取りではなく、クリップボードへ保存する処理やEmacsのkill ringは実装しません。Ctrl+Kは対象アプリのShift+Endで選択し、Deleteで削除します。折り返し行や既存の選択範囲がある場合の挙動も対象アプリに従うため、Emacsの論理行単位の削除と完全に同一ではありません。

Altを使う上記の操作を実行した後にAltを離しても、IME切り替えを送りません。それ以外のAlt単独操作・組み合わせは従来の動作を保ちます。

`表示設定` では、切り替え時の「通常モード」「Emacsモード」の画面表示をON／OFFできます（既定値はON）。表示は約1.2秒で消え、入力中のウィンドウからフォーカスを移しません。画面表示がOFFでも、通知領域のメニューとアイコンのツールチップで状態を確認できます。トレイの「有効」をOFFにすると、Alt変換とEmacs機能の両方を停止します。

### 長時間稼働時のフック維持

入力フックは設定画面とは別の専用スレッドで処理し、診断ログの書き込みと通知表示はフックから切り離しています。
Windows が通知なくフックを解除した場合に備え、30 秒ごとに再登録を予約し、1 秒以上無入力で、キー・マウスボタンが押されておらず、Alt・Emacs操作の途中でもない時点で再登録します。操作が続いている間は再登録を延期します。Emacsの選択モードは再登録後も維持します。
スリープ復帰・ロック解除・「有効」のオフ→オン時にも再登録を予約します。

## ビルドと実行

```powershell
dotnet build .\AltHenkan.sln
dotnet run --project .\tests\AltHenkan.LogicTests\AltHenkan.LogicTests.csproj
dotnet run --project .\src\AltHenkan\AltHenkan.csproj
```

Windows フックの登録・解除・無入力時の自動再登録も検証する場合は、`dotnet run --project .\tests\AltHenkan.LogicTests\AltHenkan.LogicTests.csproj -- --native-hooks` を実行します。検証用フックは無効状態で入力を通過させ、キー入力は送信しません。自動再登録の検証中はキーやマウスを操作しないでください。

### リリースビルドとインストーラー

必要なもの: .NET 8 SDK 以降。インストーラーの作成には Inno Setup 6、署名には Windows SDK の signtool.exe。

```powershell
# uiAccess マニフェスト付きの自己完結・単一ファイル publish
# (src\AltHenkan\bin\Release\net8.0-windows\win-x64\publish\AltHenkan.exe。署名して Program Files 配下に置いたときだけ起動できる)
.\scripts\build-release.ps1

# 署名付きリリースインストーラー(installer_output\AltHenkan_Setup_<version>.exe)
$env:CODESIGN_CERT = "<拇印 | サブジェクト名 | path\to\cert.pfx>"
$env:CODESIGN_PASSWORD = "<pfx のパスワード。.pfx のときのみ>"      # 任意
$env:CODESIGN_TIMESTAMP_URL = "http://timestamp.digicert.com"     # 任意(既定値)
.\scripts\build-installer.ps1 -Sign

# uiAccess なし・署名なしのユーザー単位インストーラー(管理者権限のウィンドウでは動作しない)
.\scripts\build-installer.ps1 -NoUiAccess
```

| オプション | 効果 |
|---|---|
| `-Sign` | signtool で AltHenkan.exe に署名し、Inno Setup でインストーラーとアンインストーラーにも署名する。`-NoUiAccess` を付けない限り必須。 |
| `-NoUiAccess` | uiAccess マニフェストなしの実行ファイルと、ユーザー単位のインストーラーを作る。署名は任意。 |
| `-SkipPublish` | `build-release.ps1` を実行せず、既存の publish 出力を使う。 |

`-Sign` と `-NoUiAccess` のどちらも指定しない場合は、署名の必要性と実行例を表示し、例外を出さずに終了します。ビルド・署名・インストーラー作成は行いません。

たとえば、オプションなしで実行すると次の案内が表示されます。

```text
インストーラーの作成は行いません。既定のuiAccess版は、署名なしでは起動できません。
署名付きで作成する場合： .\scripts\build-installer.ps1 -Sign
署名なしで作成する場合： .\scripts\build-installer.ps1 -NoUiAccess
※ -NoUiAccess版は、管理者権限のアプリではキー変換を使えません。
```

署名付きで作成する場合は `CODESIGN_CERT` を設定して `-Sign` を指定してください。署名なしで作成する場合は `-NoUiAccess` を指定してください。オプション不足時の案内表示だけが例外なしで終了する対象で、証明書の設定不備やビルド・署名の失敗などは引き続きエラーとして報告します。

アイコン(`src/AltHenkan/AltHenkan.ico`)は `python scripts\make-icon.py` で再生成できます(Pillow が必要)。

## 診断

Alt の操作が効かないときは、起動中の Alt Henkan をすべて終了し、`--diagnostics` を付けて起動してください。ソースから起動する場合:

```powershell
dotnet run --project .\src\AltHenkan\AltHenkan.csproj --configuration Release -- --diagnostics
```

インストール済みの場合:

```powershell
Start-Process "C:\Program Files\AltHenkan\AltHenkan.exe" -ArgumentList "--diagnostics"
```

左右の Alt を一度ずつ押し、通知領域のメニューからアプリを終了して、`%LOCALAPPDATA%\AltHenkan\AltHenkan-diagnostics.log` を確認してください。ログにはAltの処理、モード切り替え、Emacs操作の種類、入力注入の結果が記録され、通常入力の文字は記録されません。

フックの再登録世代・コールバック回数・遅いコールバックも記録します。ログ書き込みは容量制限付き非同期キューを使用し、ディスクが遅い場合はログを省略して入力処理を優先します。ログファイルへ書き込めない場合もアプリの入力処理は継続します。

## 制限事項

- 署名付きの uiAccess ビルド(「管理者権限のウィンドウについて」を参照)でない場合、より高い整合性レベルで動作しているアプリが前面にある間は、Windows によって入力の監視も送信も遮断されます。
- キーボードフックはセキュアデスクトップ(UAC の同意画面など)では動作しません。
- `VK_IME_ON` / `VK_IME_OFF` は Microsoft IME で対応しています。他社製 IME での対応は未確認です。
