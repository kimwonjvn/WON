# WON AI 어시스턴트 시작 가이드 · AI Assistant Guide

[한국어](#한국어) · [English](#english)

---

## 한국어

WON의 AI 어시스턴트는 "이 폴더에서 가장 큰 파일 10개 찾아줘", "확장자별로 폴더를 만들어 정리해줘" 같은 말을 알아듣고 **정리 계획을 제안**하는 채팅 창입니다. 계획은 **사용자가 체크하고 승인해야만** 실행되며, 삭제는 휴지통으로 가고 `Ctrl+Z`로 되돌릴 수 있습니다.

### 0. 먼저 고르세요 — 어떤 AI에 연결할까?

| | 로컬 AI (Ollama) | Claude / OpenAI (클라우드) |
|---|---|---|
| 비용 | 무료 | 쓴 만큼 요금 (API 키 필요) |
| 내 파일 정보 | **PC 밖으로 나가지 않음** | 폴더·파일 이름/경로/크기/날짜가 해당 회사 서버로 전송됨 |
| 인터넷 | 처음 모델을 받을 때만 | 항상 필요 |
| 성능 | 내 PC 사양에 따라 다름. 작은 모델은 정리 계획이 서툴 수 있음 | 대체로 더 정확 |
| 설치 | Ollama 설치 + 모델 내려받기(수 GB) | 키 입력만 |

> 회사 PC라면 **프로그램 설치와 외부 서비스 사용이 허용되는지 먼저 확인**하세요.

### 1. 어시스턴트 켜기 (모든 방식 공통)

1. WON 위쪽 도구 줄에서 **분할 보기 버튼 오른쪽의 말풍선 아이콘**을 누르거나 `Ctrl+Shift+K`를 누릅니다. 오른쪽에서 대화 창이 열립니다.
2. 대화 창 위쪽의 **톱니 아이콘(설정)**을 누릅니다.
3. **"어시스턴트 사용"** 스위치를 **켭니다.** (기본값은 꺼짐입니다. 이걸 켜지 않으면 아무것도 동작하지 않습니다.)

### 2-A. 로컬 AI로 연결하기 (Ollama) — 무료

WON의 설정 안에 **"로컬 AI 연결 도우미"**가 있어서 아래 순서를 그대로 따라가면 됩니다.

1. 대화 창 **오른쪽 아래의 모델 이름 버튼**(예: `Sonnet 5.5`)을 누르고 **Local (Ollama) → Qwen 3.5 9B**를 고릅니다.
   톱니(설정)를 열면 **"로컬 AI 연결 도우미"**가 보입니다. 열릴 때 자동으로 연결 상태를 확인합니다.
2. **[Ollama 내려받기]**를 누르면 브라우저에서 공식 사이트(ollama.com/download)가 열립니다. **Windows용 설치 파일**을 받아 실행합니다.
   - 대개 관리자 권한 없이 설치됩니다(회사 정책에 따라 다를 수 있음). 설치가 끝나면 Ollama가 자동으로 실행되어 작업 표시줄 오른쪽 아래(숨겨진 아이콘)에 나타납니다.
3. WON으로 돌아와 **[연결 확인]**을 누릅니다.
   - `✓ 준비 완료`가 나오면 끝입니다. 5번으로 가세요.
   - `✗ 모델이 아직 없습니다`가 나오면 4번으로 갑니다.
   - `✗ Ollama에 연결할 수 없습니다`가 나오면 아래 [문제 해결](#문제-해결)을 보세요.
4. **[모델 받기]**를 누르고 확인 창에서 **내려받기**를 누릅니다.
   - Qwen 3.5 9B는 **약 6.6GB**입니다. 디스크 여유 공간과 인터넷을 확인하세요.
   - 진행률이 표시됩니다. 받는 동안 WON을 계속 써도 됩니다. 중간에 **[받기 취소]**를 눌러도 나중에 이어서 받습니다.
   - 끝나면 자동으로 다시 확인해서 `✓ 준비 완료`가 나옵니다.
5. 입력창에 질문을 쓰고 `Enter`를 누릅니다. (줄바꿈은 `Shift+Enter`)

**처음 한 번은 답이 느립니다.** 모델을 메모리에 올리는 데 약 1분이 걸렸습니다(시험한 PC 기준). 이후에는 빠르고, 몇 분 쓰지 않으면 Ollama가 메모리에서 내립니다.

#### 내 PC에는 어떤 모델이 맞을까?

저희가 실제로 시험한 환경은 **RTX 3050(그래픽 메모리 8GB) + RAM 32GB**이고, 그 PC에서 `qwen3.5:9b`(6.6GB)가 그래픽 카드만으로 빠르게 돌았습니다. 아래 표의 나머지 값은 **대략적인 추정이며 직접 시험하지 않았습니다.**

| 모델 | 대략 용량 | 이럴 때 |
|---|---|---|
| `qwen3.5:9b` (기본) | 6.6GB (시험함) | 그래픽 메모리 8GB 이상, 또는 RAM 16GB 이상 |
| `qwen3.5:4b` | 3~4GB (추정) | 사양이 낮을 때. 정리 계획의 정확도는 떨어질 수 있음 |
| `qwen3.5:27b` / `qwen3.8:27b` | 17GB 안팎 (추정) | RAM 32GB 이상. 그래픽 메모리를 넘으면 느려짐 |
| `gpt-oss:20b`, `gemma4:e4b` | 미확인 | 대안. 모델 메뉴에 들어 있음 |

- 그래픽 카드가 없으면 CPU로 돌아가 **더 느립니다**(속도는 측정하지 않았습니다).
- 다른 모델을 쓰려면 모델 메뉴의 **직접 입력…**에 Ollama 모델 이름을 넣으세요. 단, **도구 호출(tools)을 지원하는 모델**이어야 폴더 작업이 됩니다. 도우미의 `연결 확인`이 지원 여부를 알려 줍니다.
- 직접 설치하고 싶다면 명령 프롬프트에서: `ollama pull qwen3.5:9b` / 설치된 모델 보기: `ollama list`

### 2-B. Claude 또는 OpenAI(클라우드)로 연결하기

1. API 키를 발급받습니다.
   - Claude: Anthropic Console(console.anthropic.com) → 결제 수단 등록·크레딧 충전 → **API Keys → Create Key**
   - OpenAI: platform.openai.com → 결제 설정 → API keys
   - Claude/ChatGPT **구독과 API는 따로**입니다. API는 쓴 만큼 요금이 나옵니다. **월 사용 한도를 꼭 걸어 두세요.**
2. WON 대화 창의 모델 메뉴에서 **Claude** 또는 **OpenAI**의 모델을 고릅니다.
3. 톱니(설정)에서 키를 붙여넣고 **[키 저장]**을 누릅니다. 키는 Windows 자격 증명에만 저장되고 설정 파일·로그·GitHub에는 남지 않습니다.
4. 키를 남에게 보여 주지 마세요. 유출되면 콘솔에서 그 키를 삭제하고 새로 만드세요.

### 3. 이렇게 써 보세요

- 먼저 **정리하고 싶은 폴더를 WON에서 열어 두세요.** 어시스턴트는 지금 열려 있는 폴더를 기준으로 봅니다(대화 창 위쪽에 경로가 표시됩니다).
- 예시: "이 폴더에서 가장 큰 파일 10개 찾아줘" · "이 파일들을 종류별 폴더로 정리하는 계획을 세워줘" · "90일 넘은 *.log 파일을 찾아줘"
- 변경이 필요하면 **계획 카드**가 나옵니다. 하고 싶은 항목만 체크하고 **[체크한 항목 실행]**을 누르세요. 마음에 안 들면 **[버리기]**. 실행 후 `Ctrl+Z`로 되돌릴 수 있습니다.
- 파일·이미지를 끌어다 놓거나 클립(📎) 버튼, `Ctrl+V`로 **첨부**할 수 있습니다. 첨부한 내용은 선택한 AI에 전달됩니다(로컬 AI면 PC 안에서만).
- **스킬**: 자주 쓰는 지침(예: "항상 날짜별 폴더로 정리")을 텍스트로 추가할 수 있습니다. 스킬은 새 권한을 주지 못합니다.
- 지난 대화는 시계 버튼에서 다시 열거나 지울 수 있습니다(이 PC에만 저장).

#### 안전 장치

- 기본값은 **꺼짐**입니다.
- AI는 폴더를 **살펴보기만** 하고, 변경은 계획으로만 올립니다. 승인한 항목만 실행됩니다.
- 드라이브 루트, Windows, Program Files, 사용자 폴더 자체 등은 **변경할 수 없습니다.** 이미 있는 파일을 덮어쓰는 작업도 거부합니다.
- 개인 키·인증서 파일(`.pem`, `.key`, `.pfx` 등)은 첨부할 수 없습니다.
- 로컬 AI는 `localhost`(이 PC)만 허용합니다.

### 문제 해결

| 증상 | 원인과 해결 |
|---|---|
| `✗ Ollama에 연결할 수 없습니다` | Ollama가 설치되지 않았거나 꺼져 있습니다. 설치 후 **시작 메뉴 → Ollama**를 실행하고 **[연결 확인]**을 다시 누르세요. 브라우저에서 `http://localhost:11434` 를 열어 "Ollama is running"이 보이면 정상입니다. |
| `✗ 모델이 아직 없습니다` | **[모델 받기]**를 누르세요. |
| 모델 받기가 멈춘 것 같음 | 용량이 커서 오래 걸립니다. 진행률이 움직이면 정상입니다. 안 움직이면 **[받기 취소]** 후 다시 누르면 이어받습니다. |
| `✗ 폴더 작업(도구 호출)을 지원하지 않습니다` | 모델 메뉴에서 `qwen3.5:9b` 같은 도구 호출 모델을 고르세요. |
| 첫 답이 1분 넘게 걸림 | 모델을 처음 메모리에 올리는 시간입니다. 두 번째부터 빨라집니다. 계속 느리면 더 작은 모델을 쓰세요. |
| "메모리가 부족합니다" | 더 작은 모델(예: `qwen3.5:4b`)을 쓰거나 다른 프로그램을 닫으세요. |
| 정리 계획이 엉뚱함 | 작은 모델의 한계일 수 있습니다. 계획 카드를 꼭 읽고 이상한 항목은 체크를 빼거나 **버리기**를 누르세요. 더 큰 모델이나 Claude/OpenAI가 더 정확합니다. |
| 대화 창이 "어시스턴트가 꺼져 있습니다" | 설정(톱니)에서 **어시스턴트 사용**을 켜세요. |
| 회사에서 Ollama 설치가 막힘 | IT 담당자에게 문의하세요. 설치하지 않고 쓰려면 Claude/OpenAI 방식을 쓰세요(회사 정책 확인 필요). |

설치·연결이 안 될 때 `Win+R` → `%TEMP%` 로 열어 `WON-setup.log`(설치 파일 기록)가 있는지도 확인해 보세요.

### 지우기

- 모델 지우기: 명령 프롬프트에서 `ollama rm qwen3.5:9b`
- Ollama 지우기: Windows **설정 → 앱 → 설치된 앱 → Ollama**. 받은 모델 파일은 보통 `%USERPROFILE%\.ollama\models`에 있습니다.
- 대화 기록 지우기: 대화 창의 시계 버튼 → **모든 대화 삭제**
- Claude/OpenAI 키 지우기: 설정에서 **[키 삭제]**

---

## English

WON's AI assistant is a chat panel that understands requests like "find the 10 largest files in this folder" and **proposes a plan**. Changes run **only after you tick and approve them**; deletes go to the Recycle Bin and `Ctrl+Z` undoes.

### 0. Choose how to connect

| | Local AI (Ollama) | Claude / OpenAI (cloud) |
|---|---|---|
| Cost | Free | Pay per use (API key) |
| Your file info | **Never leaves your PC** | Folder/file names, paths, sizes, dates are sent to that company |
| Internet | Only to download the model once | Always |
| Quality | Depends on your PC; small models plan less reliably | Usually better |
| Setup | Install Ollama + download a model (a few GB) | Paste a key |

> On a work PC, check that installing software and using external services is allowed.

### 1. Turn it on (all options)

1. Click the **speech-bubble button right of Split View** (or press `Ctrl+Shift+K`).
2. Click the **gear** at the top of the panel.
3. Switch **Enable assistant** **on**. It is off by default; nothing works until you do.

### 2-A. Local AI with Ollama (free)

The settings contain a **Local AI setup helper**. Follow it in order:

1. Click the **model button at the bottom right** and pick **Local (Ollama) → Qwen 3.5 9B**. Open the gear: the helper appears and checks the connection automatically.
2. Click **Download Ollama** (opens ollama.com/download), run the Windows installer. It usually needs no admin rights (may differ by company policy). Ollama then starts and sits in the tray.
3. Back in WON click **Check connection**:
   - `✓ Ready` → go to step 5.
   - `✗ model is not installed` → step 4.
   - `✗ Cannot reach Ollama` → see Troubleshooting.
4. Click **Get the model** and confirm. Qwen 3.5 9B is **about 6.6 GB**. A progress bar is shown; keep using WON meanwhile; **Cancel download** resumes later.
5. Type a request and press `Enter` (`Shift+Enter` = new line).

**The first answer is slow** (about a minute on the tested PC while the model loads); later ones are faster, and Ollama unloads the model after a few idle minutes.

#### Which model for my PC?

Tested: **RTX 3050 (8 GB VRAM) + 32 GB RAM** — `qwen3.5:9b` (6.6 GB) ran fully on the GPU. Other figures below are **rough estimates, not tested**.

| Model | Approx. size | When |
|---|---|---|
| `qwen3.5:9b` (default) | 6.6 GB (tested) | 8 GB+ VRAM or 16 GB+ RAM |
| `qwen3.5:4b` | 3–4 GB (est.) | Low-end PCs; plans may be less accurate |
| `qwen3.5:27b` / `qwen3.8:27b` | ~17 GB (est.) | 32 GB+ RAM; slow if it exceeds VRAM |
| `gpt-oss:20b`, `gemma4:e4b` | unverified | Alternatives in the model menu |

Without a GPU it runs on the CPU and is **slower** (not measured). For another model use **Custom model…**; it must support **tools**, which *Check connection* verifies. Terminal: `ollama pull qwen3.5:9b`, `ollama list`.

### 2-B. Claude or OpenAI

1. Get an API key (Anthropic Console → API Keys; platform.openai.com → API keys). A Claude/ChatGPT **subscription is separate** from the API. Set a **monthly spending limit**.
2. Pick a Claude or OpenAI model in the model menu.
3. Paste the key in the settings and click **Save key**. It is stored in Windows Credential Manager only — never in settings, logs or the repo.
4. If a key leaks, delete it in the console and create a new one.

### 3. Using it

- **Open the folder you want to organise in WON first** — the assistant works on the currently open folder (shown at the top of the panel).
- Try: "Find the 10 largest files" · "Plan how to sort these files into folders by type" · "Find *.log files older than 90 days".
- A **plan card** appears for changes: tick what you want, **Run checked**, or **Discard**. `Ctrl+Z` undoes.
- Attach files/images by drag & drop, the paperclip, or `Ctrl+V` (they go to the chosen AI; with local AI they stay on your PC). **Skills** add your own text instructions and cannot grant new permissions. Past chats: clock button.

Safety: off by default; the AI can only look around; protected locations (drive roots, Windows, Program Files, your profile folder) cannot be changed; overwriting is refused; key/certificate files cannot be attached; local AI only talks to `localhost`.

### Troubleshooting

| Symptom | Fix |
|---|---|
| `✗ Cannot reach Ollama` | Not installed or not running. Install it, start **Ollama** from the Start menu, press **Check connection**. Opening `http://localhost:11434` in a browser should say "Ollama is running". |
| `✗ model is not installed` | Click **Get the model**. |
| Download seems stuck | Large files take a while; if the bar moves it is fine. Otherwise **Cancel download**, then click again to resume. |
| `✗ cannot use tools` | Pick a tool-capable model such as `qwen3.5:9b`. |
| First answer takes > 1 minute | Model loading; later answers are faster. If always slow, use a smaller model. |
| "Not enough memory" | Use a smaller model (e.g. `qwen3.5:4b`) or close other programs. |
| Odd plans | Small-model limits. Read the plan card, untick or discard. Larger models or Claude/OpenAI are more accurate. |
| "The assistant is turned off" | Enable it in the gear settings. |
| Ollama blocked at work | Ask IT, or use Claude/OpenAI if policy allows. |

### Removing things

`ollama rm qwen3.5:9b` · uninstall Ollama in Windows **Settings → Apps** (models are usually in `%USERPROFILE%\.ollama\models`) · chat history: clock button → **Delete all chats** · cloud key: **Remove key** in the settings.
