# Architecture

## Layers

- Core: quota snapshot、quota window、overage、freshness、source confidence、fetch status の副作用なしの契約。
- Application: provider を束ねる dashboard refresh、30 日 history、settings、credential lifecycle、export のユースケース。
- Infrastructure: ChatGPT/Claude の manual URL、OpenCode Go usage endpoint、Copilot の gh/device flow/manual fallback、OS credential backend、JSONL repository。
- UI: Avalonia の dashboard/history/settings、tray、notification と fallback。ViewModel は Application 契約を呼びます。

## Data flow

1. UI が refresh または scheduler tick を Application に依頼。
2. Adapter は provider ごとの方式で quota 値を取得し、source（manual/official/delayed/experimental）、取得時刻、reset window、fresh-until を付ける。
3. Application は Core の契約で overage と stale を計算し、UI model に投影する。
4. snapshot は token や生 response を含めず JSONL history に append する。dashboard は current snapshot、history は日付範囲を読む。
5. tray/notification は同じ要約を利用し、OS backend が無い場合は UI fallback を使う。

## Semantics

- source confidence は取得方式の事実を表す。manual はユーザー入力、official は provider の公式 endpoint、delayed は provider が示す遅延値、experimental は未保証 adapter。
- `FreshUntil` を過ぎた値は stale と表示する。値が無い場合は unknown であり、ゼロに変換しない。
- 使用量が limit を超えた場合の overage は負値へ clamp せず、超過を明示する。
- window の終了時刻は exclusive。reset-at は provider が返した時刻を保持する。
- provider により window、limit、used の一部が unavailable なら、利用可能な部分だけを表示し、推測しない。

## Paths and settings

Application data root は OS の user application-data location から決め、machine local absolute path を設定へ埋め込みません。settings は runtime の provider URL、表示言語（ja/en）、refresh 間隔、GitHub OAuth App Client ID を保持します。Client ID は未提供のため既定値をコードへ hardcode しません。history は root 下の日付 JSONL を 30 日だけ保持します。

## Security boundaries

Infrastructure の credential adapter だけが secure store と外部 process/API 境界へアクセスします。Application/UI は opaque な credential reference と状態だけを扱い、secret value を log、UI、history、export へ渡しません。CLI 内部 credential の探索や平文 fallback は禁止です。export は quota snapshot の公開可能な値だけを出力します。

## AOT constraints

UI csproj の NativeAOT publish を対象 OS 上で行います。Avalonia compiled bindings を既定にし、JSON の polymorphic/reflective serialization は source-generated context を使います。動的 assembly loading、実行時の型探索、trim に弱い reflection 依存を新規追加しません。InvariantGlobalization や警告抑制で AOT の問題を隠しません。

## Provider adapters

- ChatGPTAdapter と ClaudeAdapter は official URL を開く manual adapter。アプリが請求 API や内部 API を模倣しません。
- OpenCodeGoAdapter は明示的な API key を secure store から読み、usage endpoint を呼びます。endpoint が遅延または quota を返さない場合は delayed/unknown。
- CopilotAdapter は `gh auth` または device flow の認証状態を使います。quota endpoint が無い・権限が無い場合は、認証成功を quota 成功と誤表示せず manual に切り替えます。
