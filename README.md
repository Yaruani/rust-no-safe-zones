# NoSafeZones

Rustのセーフゾーン判定を無効化し、指定範囲にある警備NPCやマップ設備を除去するCarbon／Oxide（uMod）向けプラグインです。

## 動作

- `Remove Safe Zone Triggers` が有効なら `TriggerSafeZone` と対応するコライダーを無効化します。
- `Remove Scientists and Guards` が有効なら、セーフゾーン内と境界から指定距離内で、対象の警備NPCを削除し、NPCスポナーを停止します。
- `Remove Turrets and SAM Sites` が有効なら、同じ範囲内の対象タレットやSAMサイトを削除します。プレイヤー所有のタレットは除外されます。
- 無効化またはアンロード時に、元のセーフゾーン判定と停止したNPCスポナーを戻します。タレットやSAMサイトの復元には保存済みのバックアップを使います。

## インストール

[NoSafeZones.cs](NoSafeZones.cs) をCarbonでは `carbon/plugins`、Oxideでは `oxide/plugins` に配置します。
初回にバックアップが空の場合は、プラグインの効果を適用する前に対象設備をスキャンします。

## チャットコマンド

サーバー管理者、または `nosafezones.admin` 権限を持つプレイヤーが使用できます。
操作はゲーム内チャットで行います。旧手順の `/nsz zone`、`/nsz npc`、`/nsz turret` は、現行版ではいずれも `/nsz` と同じ全体切り替えとして動作します。処理ごとの有効・無効は設定ファイルで変更します。

| コマンド | 動作 |
|---|---|
| `/nsz` | 実行中の効果を有効・無効に切り替えます。起動時の動作は設定の `Enable Plugin (Master Switch)` で指定します。 |
| `/nsz debug` | `Debug Mode` を切り替え、設定に保存します。 |
| `/nsz resetbackup` | 現在のバックアップを消去し、マップに存在する対象設備を再スキャンします。効果を無効化してタレットなどが実際に存在する状態で実行してください。 |

Oxideでの権限付与例。

```text
oxide.grant user <SteamID> nosafezones.admin
```

## 設定

Carbonでは `carbon/configs/NoSafeZones.json`、Oxideでは `oxide/config/NoSafeZones.json` を編集します。

```json
{
  "Enable Plugin (Master Switch)": true,
  "Remove Safe Zone Triggers": true,
  "Remove Scientists and Guards": true,
  "Remove Shopkeepers": false,
  "Remove Turrets and SAM Sites": true,
  "Force Restore Turrets": true,
  "Border Tolerance (Meters)": 5.0,
  "Debug Mode": false
}
```

- `Border Tolerance (Meters)` は、実際のセーフゾーン境界から外側へ対象範囲を広げる距離です。近くにNPC拠点を生成する場合は、この距離を調整してください。
- `Remove Shopkeepers` は設定ファイルに出力されますが、現行実装では店員やミッション担当NPCを削除する切り替えとしては機能しません。
- `Force Restore Turrets` を有効にすると、復元位置の近くに所有者なしの同じPrefabの対象設備が1基だけある場合に置き換えます。プレイヤー所有の設備、異なる設備、複数の設備がある場所は復元を見送ります。

## バックアップ

Carbonでは `carbon/data/NoSafeZones_Backup.json`、Oxideでは `oxide/data/NoSafeZones_Backup.json` に、対象設備のPrefab、座標、回転、スキンIDが保存されます。
マップのシードまたはサイズが変わるとバックアップを初期化します。

## ライセンス

[MIT License](LICENSE)
