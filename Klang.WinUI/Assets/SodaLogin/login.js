// Klang login page. Protocol field names were checked against publicly distributed
// Soda PC 3.7.0 resources and the projects listed in SOURCES.md. No client code is embedded.
(() => {
  'use strict';
  const ORIGIN = 'https://api.qishui.com';
  if (location.origin !== ORIGIN) return;
  const config = window.klangLoginConfig;
  const state = window.klangLoginState = { status: 'loading', qr: '', message: '正在连接汽水音乐…' };
  const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
  const message = text => { state.message = text; document.getElementById('status').textContent = text; };
  const error = (text, code = 'login') => Object.assign(new Error(text), { code });
  const scriptHosts = new Set(['lf-headquarters-speed.yhgfb-cn-static.com', 'lf-rc1.yhgfb-cn-static.com',
    'lf-cdn-tos.bytescm.com', 'lf3-cdn-tos.bytescm.com', 'lf6-cdn-tos.bytescm.com',
    'lf-luna.qishui.com', 'auth.zijieapi.com', 'unpkg.com']);
  function scriptUrl(value) {
    const url = new URL(value);
    if (url.protocol !== 'https:' || url.username || url.password || url.port || !scriptHosts.has(url.hostname))
      throw error('官方验证组件的来源尚不受支持，请稍后重试。', 'verification');
    return url;
  }
  async function script(value) {
    const url = scriptUrl(value);
    await new Promise((resolve, reject) => {
      const element = document.createElement('script');
      const timer = setTimeout(() => { element.remove(); reject(error('登录组件加载超时，请检查网络。', 'network')); }, 20000);
      element.onload = () => { clearTimeout(timer); resolve(); };
      element.onerror = () => { clearTimeout(timer); element.remove(); reject(error('登录组件未能加载，请检查网络。', 'network')); };
      element.src = url.href;
      document.head.appendChild(element);
    });
  }
  async function request(path, body, flow = {}, json = false) {
    const url = new URL(path, ORIGIN);
    if (url.origin !== ORIGIN) throw error('登录请求来源无效。');
    const headers = { Accept: 'application/json' };
    if (url.pathname.startsWith('/passport/')) {
      url.searchParams.set('p_zt', window.$SECURE_VERSION || '0');
      url.searchParams.set('p_bd', window._sdkGlueVersionMap?.bdmsVersion || '0');
      url.searchParams.set('request_host', encodeURIComponent(ORIGIN));
      const csrf = document.cookie.match(/(?:^|;\s*)passport_csrf_token(?:_default)?=([^;]+)/);
      if (csrf) headers['x-tt-passport-csrf-token'] = csrf[1];
      if (flow.trace) headers['x-tt-passport-trace-id'] = flow.trace;
      if (flow.portrait) headers['x-tt-passport-verify-portrait'] = flow.portrait;
    }
    if (body !== undefined) headers['Content-Type'] = json ? 'application/json' : 'application/x-www-form-urlencoded';
    let response;
    try {
      response = await fetch(url.href, { method: body === undefined ? 'GET' : 'POST', headers,
        body, credentials: 'include', redirect: 'error', signal: AbortSignal.timeout(20000) });
    } catch { throw error('无法连接汽水登录服务，请检查网络后重试。', 'network'); }
    if (!response.ok) throw error('汽水登录服务暂时不可用，请稍后重试。', 'network');
    const text = await response.text();
    if (text.length > 1048576) throw error('汽水返回了异常的登录数据。');
    try { return JSON.parse(text); } catch { throw error('汽水返回了空白或无效的登录数据。'); }
  }
  function challenge(value) {
    const nested = value?.data || {};
    for (const item of [nested, value]) {
      if (Number(item?.error_code) === 2046 && typeof item.url === 'string')
        return { modern: true, decision: item, body: item.biz_params };
    }
    const encoded = nested.verify_center_decision_conf || nested.verify_center_secondary_decision_conf ||
      value?.verify_center_decision_conf || value?.verify_center_secondary_decision_conf;
    if (typeof encoded !== 'string' || encoded.length > 131072) return null;
    let decision;
    try { decision = JSON.parse(encoded); } catch { return null; }
    if (![10000, 20000].includes(Number(decision.code))) return null;
    return { modern: false, decision, body: nested.sms_code_key ? { sms_code_key: nested.sms_code_key } : undefined };
  }
  function check(value) {
    const nested = value?.data || {};
    const codes = [value?.status_code, value?.error_code, nested.error_code].filter(x => x !== undefined && x !== null && x !== '');
    const bad = codes.find(x => !Number.isFinite(Number(x)) || Number(x) !== 0);
    if (!value || typeof value !== 'object' || bad !== undefined || (value.message && value.message !== 'success'))
      throw error('汽水尚未完成本次登录，请重新获取二维码。', 'rejected');
    return value;
  }
  async function verify(challenge, flow) {
    state.status = 'verifying';
    document.getElementById('qr').hidden = true;
    message('请在此窗口完成汽水音乐的安全验证。');
    return await new Promise(async (resolve, reject) => {
      let done = false;
      const finish = (ok, fp = '') => {
        if (done) return;
        done = true; clearTimeout(timer);
        if (ok) resolve(fp); else reject(error('安全验证已取消，登录尚未完成。', 'verification'));
      };
      const timer = setTimeout(() => { if (!done) { done = true; reject(error('安全验证已超时，请重新登录。', 'verification')); } }, 180000);
      const generalParams = { device_id: config.deviceId, install_id: config.installId,
        did: config.deviceId, iid: config.installId, device_platform: 'PC', version_code: config.versionCode };
      try {
        if (challenge.modern) {
          if (!window.React || !window.ReactDOM) {
            await script('https://unpkg.com/react@18.3.1/umd/react.production.min.js');
            await script('https://unpkg.com/react-dom@18.3.1/umd/react-dom.production.min.js');
          }
          window.ucSecondVerifyReact = window.React;
          window.ucSecondVerifyReactDom = window.ReactDOM;
          window.$$account_verify_portrait_id = flow.portrait;
          const entry = scriptUrl(challenge.decision.url);
          entry.searchParams.set('aid', '386088');
          entry.searchParams.set('verify_reason', challenge.decision.event_params?.verify_reason || '');
          entry.searchParams.set('verify_scene', challenge.decision.event_params?.verify_scene || '');
          await script(entry.href);
          if (typeof window.ucWebSecondVerify !== 'function') throw error('安全验证组件尚未就绪。', 'verification');
          window.ucWebSecondVerify({ ...challenge.decision, aid: 386088, did: config.deviceId, iid: config.installId,
            generalParams, verify_portrait_id: flow.portrait, fun: 'verify', uc_account_verify_version: '1.0.29',
            newSecondVerifyWebOptions: { host: ORIGIN, verifyAppName: '汽水音乐' },
            monitorTime: { startTime: Date.now(), scriptLoadStartTime: 0, scriptLoadEndTime: Date.now(), renderStartTime: Date.now() },
            verifyFinishCallback: result => finish(result?.status === true) });
        } else {
          await script('https://lf-rc1.yhgfb-cn-static.com/obj/rc-client-security/secsdk-captcha/2.28.11/captcha.js');
          const sdk = window.verifySDK;
          if (typeof sdk?.initVerifyOptions !== 'function' || typeof sdk.autoRender !== 'function') throw error('安全验证组件尚未就绪。', 'verification');
          sdk.initVerifyOptions({ commonOptions: { aid: 386088, did: config.deviceId, iid: config.installId },
            captchaOptions: { sideSlide: 'disabled', lang: 'zh', showMode: 'mask', region: 'cn', app_name: '' },
            secondVerifyWebOptions: { host: ORIGIN, verifyAppName: '汽水音乐' } });
          sdk.autoRender({ verify_data: challenge.decision,
            captchaOptions: { successCb: () => finish(true, typeof sdk.getCaptchaWebId === 'function' ? sdk.getCaptchaWebId() : ''), closeCb: () => finish(false) },
            secondVerifyWebOptions: { scene: '4', callBack: () => finish(true), closeCallBack: () => finish(false) } });
        }
      } catch (problem) { if (!done) { done = true; clearTimeout(timer); reject(problem); } }
    });
  }
  async function passport(path, body, flow) {
    let value = await request(path, body, flow);
    const needed = challenge(value);
    if (needed) {
      const fp = await verify(needed, flow);
      const url = new URL(path, ORIGIN);
      url.searchParams.set('isResend', 'true');
      if (fp) { url.searchParams.set('fp', fp); url.searchParams.set('verifyFp', fp); }
      if (needed.body && typeof needed.body === 'object' && !Array.isArray(needed.body)) {
        const form = body === undefined ? url.searchParams : new URLSearchParams(body);
        for (const [key, part] of Object.entries(needed.body)) form.set(key, typeof part === 'object' ? JSON.stringify(part) : String(part));
        if (body !== undefined) body = form.toString();
      }
      value = await request(url.pathname + url.search, body, flow);
      if (challenge(value)) throw error('汽水仍要求安全验证，请稍后重新登录。', 'verification');
      state.status = 'waiting';
      document.getElementById('qr').hidden = false;
    }
    return check(value);
  }
  window.klangSetQr = (url, image) => {
    if (url !== state.qr || typeof image !== 'string' || !image.startsWith('data:image/png;base64,')) return;
    document.getElementById('qr').src = image;
  };
  async function run() {
    await script('https://lf-headquarters-speed.yhgfb-cn-static.com/obj/rc-client-security/web/glue/1.0.0.36/sdk-glue.js');
    if (typeof window._SdkGlueInit !== 'function') throw error('汽水登录组件尚未就绪。');
    window._SdkGlueInit({ self: { aid: 386088, pageId: 24554 }, bdms: { aid: 386088, paths: ['/passport'] } });
    const initializationDeadline = Date.now() + 15000;
    while (!(typeof window.bdms?.init === 'function' && !window._SdkGlueLoadingMap?.bdms)) {
      if (Date.now() > initializationDeadline) throw error('汽水登录组件初始化超时。', 'network');
      await sleep(100);
    }
    const opts = { aid: 386088, service: 'api.qishui.com', host: ORIGIN, unionHost: '', union: false, needFid: false, fid: '' };
    const device = await request('/ttwid/check/', JSON.stringify({ ...opts, migrate_priority: 0 }), {}, true);
    if (!Number.isFinite(Number(device.status_code))) throw error('汽水设备初始化未完成。');
    if (Number(device.status_code) > 1001) {
      const registered = await request('/ttwid/register/', JSON.stringify({ ...opts, migrate_info: device.migrate_info }), {}, true);
      if (Number(registered.status_code) !== 0) throw error('汽水登录设备初始化失败。');
    }
    const trace = Array.from(crypto.getRandomValues(new Uint8Array(4)), x => x.toString(16).padStart(2, '0')).join('');
    const flow = { trace, portrait: crypto.randomUUID() + '.login' };
    document.cookie = `biz_trace_id=${trace};path=/;domain=qishui.com;Secure;SameSite=Lax`;
    const params = new URLSearchParams({ aid: '386088', passport_jssdk_version: '2.4.13', passport_jssdk_type: 'normal',
      is_from_ttaccountsdk: '1', p_js_v: '2.4.13', p_js_t: 'pro', p_ver: '1.0.29', account_sdk_source: 'web',
      language: 'zh', biz_trace_id: trace, device_id: config.deviceId, install_id: config.installId,
      did: config.deviceId, iid: config.installId, device_platform: 'PC', version_code: config.versionCode });
    const created = await passport('/passport/web/get_qrcode/?' + params + '&next=https%3A%2F%2Fapi.qishui.com&need_logo=false&need_short_url=false&is_new_login=1', undefined, flow);
    const token = created.data?.token;
    if (typeof token !== 'string' || token.length < 1 || token.length > 4096) throw error('汽水未返回有效二维码。');
    const original = new URL(created.data.qrcode_index_url);
    if (original.protocol !== 'https:' || original.hostname !== 'bff-pc.qishui.com' || original.username || original.password || original.port ||
      original.searchParams.getAll('token').length !== 1 || original.searchParams.get('token') !== token) throw error('汽水二维码未通过来源校验。');
    const qr = new URL('https://bff-pc.qishui.com/light/invoke/scan_login');
    qr.searchParams.set('token', token); qr.searchParams.set('os', 'Windows'); qr.searchParams.set('computer_name', config.computerName);
    state.qr = qr.href; state.status = 'waiting';
    message('请用汽水音乐 App「搜索 → 扫一扫」扫码，并在手机确认登录此电脑。');
    const body = new URLSearchParams({ need_logo: 'false', need_short_url: 'false', is_frontier: 'true',
      token, is_new_login: '1', next: ORIGIN }).toString();
    let polls = 0;
    while (++polls <= 90) {
      await sleep(2000);
      const result = await passport('/passport/web/check_qrconnect/?' + params + '&is_new_login=1', body, flow);
      const data = result.data || {}, code = String(data.status || '');
      if (code === '3' || code === 'confirmed' || typeof data.session_cookie === 'string' && data.session_cookie.length > 0) {
        // Some Passport versions return session_cookie instead of setting an HttpOnly cookie.
        if (typeof data.session_cookie === 'string' && data.session_cookie.length <= 65536) state.session = data.session_cookie;
        state.status = 'complete'; state.qr = ''; message('手机确认完成，正在校验账号…'); return;
      }
      if (code === '2' || code === 'scanned') message('已扫码，请在手机上确认。需要安全验证时请在此窗口继续。');
      else if (code !== '1' && code !== 'new') throw error('二维码已失效或登录被取消，请关闭后重新登录。', 'expired');
    }
    throw error('二维码已过期，请关闭后重新登录。', 'expired');
  }
  run().catch(problem => {
    state.status = 'error'; state.code = problem.code || 'login'; state.qr = ''; state.session = '';
    document.getElementById('qr').hidden = true;
    // Only our fixed error messages are displayed; never expose a raw response or URL.
    message(problem.code ? problem.message : '汽水登录未能完成，请关闭后重新登录。');
  });
})();
