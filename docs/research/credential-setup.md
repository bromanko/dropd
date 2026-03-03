# Credential Setup Guide

## Apple Music API

You need three things: a **Team ID**, a **Key ID**, and a **private key** (.p8 file). These are used to generate developer tokens (JWTs). You also need a **Music User Token** to access personal library data.

### Step 1: Create a Media Identifier

1. Sign in to [Certificates, Identifiers & Profiles](https://developer.apple.com/account/resources/)
2. Click **Identifiers** in the sidebar
3. Click the **+** button, select **Media IDs**, then click **Continue**
4. Enter a description (e.g., "dropd") and a reverse-domain identifier (e.g., `com.yourname.dropd`)
5. Enable **MusicKit**
6. Click **Continue**, review, then **Register**

### Step 2: Create a Private Key

1. In Certificates, Identifiers & Profiles, click **Keys** in the sidebar
2. Click the **+** button
3. Enter a key name (e.g., "dropd MusicKit Key")
4. Check **MusicKit** and click **Configure** — associate it with the Media ID you just created
5. Click **Continue**, then **Register**
6. **Download the .p8 file** — you can only download it once
7. Note the **Key ID** shown on the confirmation page

### Step 3: Note Your Team ID

1. Go to [Apple Developer Account](https://developer.apple.com/account)
2. Your **Team ID** is shown in the Membership section (a 10-character alphanumeric string)

### Step 4: Obtain a Music User Token

The Music User Token is needed for `/v1/me/*` endpoints (library, ratings, playlists). Unfortunately, Apple requires this to be obtained through their client libraries (MusicKit JS, MusicKit Swift, or MusicKit Android). There is no server-only flow.

**Easiest approach for a personal service:**

1. Create a minimal HTML page that loads MusicKit JS
2. Configure it with your developer token
3. Call `music.authorize()` — this prompts you to sign in with your Apple ID
4. Capture the returned Music User Token
5. Store it in your dropd secrets configuration

The Music User Token is long-lived but can expire. You may need to re-authorize periodically.

**Minimal MusicKit JS page:**

```html
<!DOCTYPE html>
<html>
<head>
  <title>dropd — Authorize Apple Music</title>
  <script src="https://js-cdn.music.apple.com/musickit/v3/musickit.js"></script>
</head>
<body>
  <button id="authorize">Authorize Apple Music</button>
  <pre id="output"></pre>
  <script>
    document.addEventListener('musickitloaded', async () => {
      const music = await MusicKit.configure({
        developerToken: 'YOUR_DEVELOPER_TOKEN_HERE',
        app: { name: 'dropd', build: '1.0.0' }
      });
      document.getElementById('authorize').addEventListener('click', async () => {
        const userToken = await music.authorize();
        document.getElementById('output').textContent = 
          'Music User Token:\n\n' + userToken;
      });
    });
  </script>
</body>
</html>
```

### Summary of Credentials Needed

| Credential | Where to store | Used for |
|---|---|---|
| Bearer Token | `DROPD_APPLE_MUSIC_TOKEN` env var | `Authorization: Bearer` header on all Apple Music requests |

The bearer token is used for both catalog endpoints and personalized `/v1/me/*` endpoints. Token provisioning (generating a developer JWT from the Team ID, Key ID, and private key, and/or obtaining a Music User Token) is handled outside of dropd.

## Last.fm API

Much simpler — you just need an API key.

### Step 1: Create a Last.fm Account

1. Go to [last.fm/join](https://www.last.fm/join) and create an account (if you don't have one)

### Step 2: Get an API Key

1. Go to [last.fm/api/account/create](https://www.last.fm/api/account/create)
2. Fill in:
   - **Application name:** dropd
   - **Application description:** Personal playlist curator
   - **Callback URL:** (leave blank)
3. Submit — you'll receive an **API Key** and a **Shared Secret**
4. You only need the **API Key** for read-only operations (which is all dropd uses)

### Summary of Credentials Needed

| Credential | Where to store | Used for |
|---|---|---|
| API Key | Config/env | `api_key` parameter on all requests |
