# Персональные источники в Cinema fork

Этот пакет — форк VizVid 1.8.2, версия `1.8.2-cinema.1`, build
`T098-AUTHORING-v1`. Upstream MIT license и авторство сохранены. Package ID
остаётся `idv.jlchntoz.vvmw`, чтобы сохранить идентичность существующих ресурсов.
Обычный VizVid и этот форк нельзя устанавливать одновременно.

Пока это проверяемый кандидат, не выпуск для основного мира. AVPro, Photon,
вывод звука и потребители текстуры требуют отдельных клиентских проверок.

## Подключение в редакторе

1. Сделайте сохранённую копию своей сцены и установите проверенную версию форка.
   Сохраняйте `.meta` вместе с исходниками. Выполните UdonSharp compile.
2. `Tools → VizVid → Source Mirrors → Install…`: явно задайте Core, канонический
   URL фильма, исключительно видео-рендереры и выходные AudioSource этого плеера.
3. Установщик принимает выходы, зарегистрированные напрямую ровно один раз в
   выбранном Core. Он отказывает при существующем AudioController, общей колонке
   с другим Core или пересечении экранов. Настроенные эффекты/контроллеры громкости
   не заменяются автоматически: их интеграция требует отдельного решения.
4. Полученная композиция выключена: controller `enabled=false`, `mirrorsOff=true`.
   Громкость проходит через пакетные gates с gain=1; обычные команды принадлежат Core.
5. `Validate composition` в контекстном меню controller проверяет граф, включая
   неактивные компоненты сцены. Та же проверка выполняется до обработки build-сцены.
   `SourceMirrorAuthoring.Install/Validate/Remove` — тот же API для генераторов сцен.

Текущий controller привязан к одному каноническому URL. Каталог фильмов и UI выбора
не создаются установщиком. Специфичные для мира права и интерфейс подключаются к API;
вторую реализацию загрузки/часов/коррекции в world glue добавлять не требуется.

## Локальный каталог (T122)

`SourceMirrorCatalog` добавляет локальный проверяемый снимок текущего фильма:
1–3 готовых VRCUrl, стабильные ID, подписи, версию и defensive copies. Он не
синхронизируется и не публикует данные самостоятельно. Без network authority
`NetworkPublicationEnabled` false.
`BindLocalContext` получает только явно предоставленный локальный контекст.
Сам local token не является shared playback identity; T123 binding описан ниже.
Установщик не создаёт каталог автоматически и не заменяет авторизацию мира.

После локального Bind/Commit задайте `controller.sourceCatalog`. `SelectSource`,
`UsePrimary` и `RetrySelected` принимают актуальные Version/ContextPlaybackId,
проверяют PC/Core context и передают URL прежнему Core transport. При Off,
disabled или незавершённом handback они отказывают без включения/загрузки.
Retry читает актуальный URL выбранного ID; удалённый ID остаётся Unavailable.
SelectedSourceId обозначает принятое намерение, ActiveSourceId — совпавший
готовый physical receipt после picture handoff, а не requested URL.

Правка подписей/порядка/URL в локальном снимке не загружает видео и не меняет
уже принятую selection receipt. Смена film context очищает чужой intent без
transport-команд. Один каталог обслуживает один controller; повторная регистрация
другого controller отклоняется. Общая шкала/команды/сроки подготовки кадра не менялись.

Catalog program asset имеет собственный стабильный GUID; при ручном добавлении
каталога его компонент также нужно снять перед физическим исключением папки.
Editor Install/Remove пока владеет только создаваемой им прежней output-композицией.
Этот локальный API не является готовым сетевым каталогом, Auto или production UI.

## Общий каталог (T123, candidate)

Core сохраняет UUID и immutable post-filter PC/effective-mobile/configured-backend
заголовок каждого принятого PlayUrl в том же snapshot. Это metadata, не новая шкала.
Loop, LocalSync и личные операции не меняют ID. Optional generic observer не даёт
Core зависимости от optional типов; без функции ordinary playback остаётся.

Вручную добавьте `SourceMirrorCatalogAuthority`, задайте Core, catalog и собственный
`SourceMirrorCatalogAccessPolicy`, а в catalog — обратную authority-ссылку.
`enablePublication` по умолчанию false. Проверьте граф через
`SourceMirrorAuthoring.ValidateCatalogAuthority`; установщик output-композиции не
назначает права мира автоматически. В network-managed режиме старые T122 local
write API закрыты; Select/Retry остаются личными.

`RequestPublishCurrentCatalog` передаёт ровно восемь typed параметров. Проверка
прав использует actual network caller и актуальную role revision на авторитетной
копии provider; SDK owner не получает права ведущего автоматически. Draft CAS,
whole-snapshot validation/copy и reentrant guards предшествуют commit. Pending
request, PublicationStatus и CatalogStatus различаются. Reply/serialization success
не означает remote ACK; подтверждение требует matching accepted snapshot.

После внезапной потери writer cache/просмотр сохраняются, writes закрыты до нового
обычного запуска с authenticated recovery marker и нового authorized Apply.
Cooperative handoff использует freeze/exact-base offer/ACK/authenticated Commit
перед передачей ownership, не локальное
`maxRevision+1`. Истёкший offer не открывает writes. Невиданный последним writer
commit не объявляется восстановленным. Both packet orders и late readiness имеют
bounded reconciliation; metadata не вызывает Play/Seek/Reload.

Для физического исключения сначала снимите вручную добавленные authority/policy и
catalog components и ссылки world glue. Они не принадлежат старому output Remove.
Новые сетевые claims требуют actual Photon/ownership/byteCount/unavailable-primary
проверки. Managed/ClientSim не заменяют её; main install/Auto/release не включены.

## Runtime On / Off

`_TurnOn` включает композицию; `_SelectMirror` использует `requestedPhysical` для
текущего `canonical`. `_WatchCoreLoads` дополнительно включает наблюдение за Core.
У фильма остаются общие команды и время; URL загрузки выбирается лично.

Для выключения вызовите `_TurnOff` и дождитесь `CanDetach`. Это конечный возврат к
каноническому источнику, а не принудительное отключение компонента во время загрузки.
STOP/новый фильм отменяют старую работу. Ошибка остаётся локальной; автоматического
повторения URL или второго декодера нет. Во время подготовки кадра выход удерживается.

## Снятие и физическое исключение

В Edit Mode выберите выключенный controller в состоянии Off и выполните
`Remove disabled composition`. Операция поддерживает Undo, снимает backing Udon
компоненты и возвращает выходы в `Core.audioSources`, сохраняя Core и остальные
маршруты. Она отказывает при pending/error/удержании или отдельном gain, отличном
от 1: такой gain нельзя молча потерять при возврате к прямому выходу.

После снятия всех композиций можно физически исключить **обе** директории с их
`.meta`: `Runtime/VVMW/SourceMirrors` и `Editor/VVMW/SourceMirrors`. Удалите ссылки
из собственной world glue и пересоберите Udon. Core и обычный плеер не имеют
зависимости от типов этих компонентов. Сам форк, его Core API и поправка paused
playback speed при этом остаются; это не откат всего пакета к upstream.

## Первый вход в комнату на паузе

На штатном пути Core зритель применяет полученную позицию при первом Ready,
если комната уже на паузе. Синхронизация выполняется один раз через существующий
`SyncTime(true)`: у такого входа нет Play callback, который применил бы время.
Владелец, playing/mapped пути и положительная позиция личной перезагрузки
сохраняют прежнюю обработку. Это исправление первого paused-ready в форке,
независимое от публикации каталога; повторная общая перемотка не требуется.
Проверка настоящего кадра и тишины остаётся отдельной клиентской приёмкой.

VideoPlayerHandler начинает обычный поиск текстуры и после Ready, если Core
сразу поставил видео на паузу. Тот же одиночный poll используется при последующем
Start. Готовый paused handler также допускается в существующий blit loop с его
паузной частотой0.2s: асинхронно обновившийся native кадр попадает в buffered output
без Play, повторной загрузки или изменения общего времени. Неактивный/ошибочный
handler прекращает эту работу по прежним guards. Это исправление вывода текстуры
в форке действует и при Off, без зависимости от необязательной композиции зеркал.

## Обновление

Встроенный updater upstream отключён: он не является способом обновления форка.
Внешний VCC/ALCOM всё ещё может заменить пакет с тем же ID; контролируйте источник
обновления. Отдельная публичная VPM listing пока не предоставляется.

Рабочий checkout `VVMW Source` использует sparse checkout исходников. Его папку
нельзя копировать как полный установочный пакет: часть upstream-ресурсов отсутствует
на диске. Для поставки нужен полный пакет из проверенного Git tree; лабораторный
runner накладывает закреплённые исходники на отдельно проверенный полный baseline.

Сначала выключите/снимите композицию, сохраните сцену и закройте редактор. Обновляйте
весь проверенный пакет вместе с `.meta`, затем выполните compile, проверку графа и
приёмку Off/On/Remove/ordinary playback на копии сцены. Не переносите сгенерированные
`Assets/SerializedUdonPrograms` из другого проекта. Четыре program assets принадлежат
пакету; их GUID стабильны, compiled programs создаёт UdonSharp текущего проекта.

При переходе с `1.8.2` проверьте ограничения версий в собственных asmdef: строгое
`[1.8.2]` не включает `1.8.2-cinema.1`. Изменяйте только проверенные потребители;
не отключайте проверки версий вообще. Неизвестный upstream требует переноса патчей
и повторной квалификации, а не автоматической установки поверх проверенной версии.
