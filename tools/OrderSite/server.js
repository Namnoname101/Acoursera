import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, isAbsolute, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const moduleDirectory = fileURLToPath(new URL('.', import.meta.url));
const defaultPublicDirectory = join(moduleDirectory, 'public');
const DEFAULT_CONTROL_SERVER = 'https://coursera-cookie-srv.fly.dev';
const MAX_REQUEST_BYTES = 64 * 1024;
const REQUEST_TIMEOUT_MS = 35_000;

const staticFiles = new Map([
  ['/', 'index.html'],
  ['/index.html', 'index.html'],
  ['/styles.css', 'styles.css'],
  ['/app.js', 'app.js'],
  ['/favicon.svg', 'favicon.svg'],
  ['/og.png', 'og.png'],
]);

const contentTypes = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.svg': 'image/svg+xml; charset=utf-8',
  '.png': 'image/png',
};

function applySecurityHeaders(response) {
  response.setHeader('x-content-type-options', 'nosniff');
  response.setHeader('x-frame-options', 'DENY');
  response.setHeader('referrer-policy', 'no-referrer');
  response.setHeader('permissions-policy', 'camera=(), microphone=(), geolocation=(), payment=()');
  response.setHeader('cross-origin-opener-policy', 'same-origin');
  response.setHeader('cross-origin-resource-policy', 'same-origin');
  response.setHeader(
    'content-security-policy',
    "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
  );
  response.setHeader('strict-transport-security', 'max-age=31536000; includeSubDomains');
}

function sendJson(response, statusCode, payload) {
  const body = Buffer.from(JSON.stringify(payload));
  response.writeHead(statusCode, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': body.length,
    'cache-control': 'no-store',
    pragma: 'no-cache',
    expires: '0',
  });
  response.end(body);
}

function apiError(response, statusCode, code, message) {
  sendJson(response, statusCode, { error: { code, message } });
}

function cleanText(value, maximum) {
  if (typeof value !== 'string') return '';
  return value.trim().slice(0, maximum);
}

function cleanOptionalText(value, maximum) {
  const cleaned = cleanText(value, maximum);
  return cleaned || null;
}

function cleanOptionalNumber(value, { minimum = 0, maximum = Number.MAX_SAFE_INTEGER } = {}) {
  if (value === null || value === undefined || value === '' || typeof value === 'boolean') return null;
  const number = Number(value);
  if (!Number.isFinite(number)) return null;
  return Math.min(maximum, Math.max(minimum, number));
}

function mapLiveOrderJob(job) {
  if (!job || typeof job !== 'object' || Array.isArray(job)) return null;
  const mode = cleanOptionalText(job.mode, 20) || 'course';
  const status = cleanOptionalText(job.status, 80) || 'queued';
  return {
    id: cleanOptionalText(job.id, 200),
    orderId: cleanOptionalText(job.orderId, 200),
    orderCode: cleanOptionalText(job.orderCode, 200),
    customerCode: cleanOptionalText(job.customerCode, 200),
    customerName: cleanOptionalText(job.customerName, 200),
    courseTitle: cleanOptionalText(job.courseTitle, 500),
    courseSlug: cleanOptionalText(job.courseSlug, 500),
    courseUrl: cleanOptionalText(job.courseUrl, 2048),
    mode,
    status,
    progress: cleanOptionalNumber(job.progress, { minimum: 0, maximum: 100 }) ?? 0,
    currentModule: cleanOptionalNumber(job.currentModule),
    totalModules: cleanOptionalNumber(job.totalModules),
    currentActivity: cleanOptionalText(job.currentActivity, 1000),
    manualActionReason: cleanOptionalText(job.manualActionReason, 1000),
    errorMessageSafe: cleanOptionalText(job.errorMessageSafe, 1000),
    attempt: cleanOptionalNumber(job.attempt, { minimum: 0, maximum: 1000 }),
    lastHeartbeat: cleanOptionalText(job.lastHeartbeat, 100),
    startedAt: cleanOptionalText(job.startedAt, 100),
    completedAt: cleanOptionalText(job.completedAt, 100),
    createdAt: cleanOptionalText(job.createdAt, 100),
    updatedAt: cleanOptionalText(job.updatedAt, 100),
    // The control server owns the device-wide lifecycle decision.  Do not try
    // to infer this capability from one job after internal device fields have
    // been stripped from the public feed.
    profileOpenable: job.profileOpenable === true,
    pauseAllowed: job.pauseAllowed === true,
    pausePending: job.pausePending === true,
    resumeAllowed: job.resumeAllowed === true,
  };
}

async function readJsonBody(request) {
  const chunks = [];
  let size = 0;
  for await (const chunk of request) {
    size += chunk.length;
    if (size > MAX_REQUEST_BYTES) {
      const error = new Error('Request body is too large.');
      error.code = 'BODY_TOO_LARGE';
      throw error;
    }
    chunks.push(chunk);
  }
  if (!chunks.length) return {};
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } catch {
    const error = new Error('Request body must contain valid JSON.');
    error.code = 'INVALID_JSON';
    throw error;
  }
}

function validateDirectOrder(input) {
  if (!input || typeof input !== 'object' || Array.isArray(input)) {
    throw Object.assign(new Error('Thông tin lên đơn không hợp lệ.'), { code: 'INVALID_ORDER' });
  }

  const googleEmail = cleanText(input.googleEmail, 320);
  const googlePassword = typeof input.googlePassword === 'string'
    ? input.googlePassword.slice(0, 1024)
    : '';
  if (!googleEmail || !googlePassword) {
    throw Object.assign(new Error('Cần nhập tài khoản và mật khẩu Google.'), {
      code: 'GOOGLE_CREDENTIALS_REQUIRED',
    });
  }

  const rawCourses = Array.isArray(input.courses) ? input.courses : [];
  if (rawCourses.length < 1 || rawCourses.length > 20) {
    throw Object.assign(new Error('Chọn từ 1 đến 20 khóa học.'), {
      code: 'COURSE_SELECTION_REQUIRED',
    });
  }

  const courses = rawCourses.map((item) => {
    const courseId = cleanText(item?.courseId, 200);
    const courseUrl = cleanText(item?.courseUrl, 2048);
    if ((!courseId && !courseUrl) || (courseId && courseUrl)) {
      throw Object.assign(new Error('Mỗi khóa phải là khóa đã lưu hoặc một URL Coursera.'), {
        code: 'COURSE_SELECTION_REQUIRED',
      });
    }
    return courseId ? { courseId } : { courseUrl };
  });

  const idempotencyKey = cleanText(input.idempotencyKey, 200);
  if (!idempotencyKey) {
    throw Object.assign(new Error('Mã chống gửi trùng bị thiếu. Hãy tải lại trang.'), {
      code: 'IDEMPOTENCY_KEY_REQUIRED',
    });
  }

  const customerName = cleanText(input.customerName, 200);
  return {
    ...(customerName ? { customerName } : {}),
    courses,
    googleEmail,
    googlePassword,
    skipGradedAppItems: input.skipGradedAppItems !== false,
    skipPracticeAppItems: input.skipPracticeAppItems !== false,
    idempotencyKey,
  };
}

async function serveStatic(response, publicDirectory, fileName) {
  const root = resolve(publicDirectory);
  const filePath = resolve(root, fileName);
  const relativePath = relative(root, filePath);
  if (relativePath.startsWith('..') || isAbsolute(relativePath)) {
    apiError(response, 404, 'NOT_FOUND', 'Không tìm thấy trang.');
    return;
  }
  try {
    const body = await readFile(filePath);
    response.writeHead(200, {
      'content-type': contentTypes[extname(filePath)] || 'application/octet-stream',
      'content-length': body.length,
      'cache-control': fileName === 'index.html' ? 'no-store' : 'public, max-age=300',
    });
    response.end(body);
  } catch (error) {
    if (error?.code === 'ENOENT') {
      apiError(response, 404, 'NOT_FOUND', 'Không tìm thấy tài nguyên.');
      return;
    }
    throw error;
  }
}

function normalizeControlServerUrl(value) {
  const url = new URL(value || DEFAULT_CONTROL_SERVER);
  if (!['https:', 'http:'].includes(url.protocol)) {
    throw new Error('CONTROL_SERVER_URL must use HTTP or HTTPS.');
  }
  return url.origin;
}

export function createOrderSiteServer(options = {}) {
  const controlServerUrl = normalizeControlServerUrl(
    options.controlServerUrl ?? process.env.CONTROL_SERVER_URL ?? DEFAULT_CONTROL_SERVER,
  );
  const publicDirectory = resolve(options.publicDirectory ?? defaultPublicDirectory);
  const fetchImplementation = options.fetchImplementation ?? globalThis.fetch;

  async function proxyAdminRequest(request, response, targetPath, body = undefined) {
    const adminKey = cleanText(request.headers['x-admin-key'], 4096);
    if (!adminKey) {
      apiError(response, 401, 'ADMIN_KEY_REQUIRED', 'Nhập Admin API key để tiếp tục.');
      return;
    }

    const headers = { 'x-admin-key': adminKey, accept: 'application/json' };
    let serializedBody;
    if (body !== undefined) {
      headers['content-type'] = 'application/json';
      serializedBody = JSON.stringify(body);
    }

    try {
      const upstream = await fetchImplementation(`${controlServerUrl}${targetPath}`, {
        method: request.method,
        headers,
        body: serializedBody,
        redirect: 'error',
        signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
      });
      serializedBody = '';
      const responseBody = Buffer.from(await upstream.arrayBuffer());
      response.writeHead(upstream.status, {
        'content-type': upstream.headers.get('content-type') || 'application/json; charset=utf-8',
        'content-length': responseBody.length,
        'cache-control': 'no-store',
        pragma: 'no-cache',
        expires: '0',
      });
      response.end(responseBody);
    } catch {
      serializedBody = '';
      apiError(
        response,
        502,
        'CONTROL_SERVER_UNAVAILABLE',
        'Chưa kết nối được máy chủ điều phối. Hãy thử lại sau ít phút.',
      );
    }
  }

  async function proxyJobActionRequest(request, response, targetPath, body = undefined) {
    const adminKey = cleanText(request.headers['x-admin-key'], 4096);
    if (!adminKey) {
      apiError(response, 401, 'ADMIN_KEY_REQUIRED', 'Nhập Admin API key để tiếp tục.');
      return;
    }

    try {
      const headers = {
        'x-admin-key': adminKey,
        accept: 'application/json',
      };
      const options = {
        method: 'POST',
        headers,
        redirect: 'error',
        signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
      };
      if (body !== undefined) {
        headers['content-type'] = 'application/json';
        options.body = JSON.stringify(body);
      }
      const upstream = await fetchImplementation(`${controlServerUrl}${targetPath}`, options);
      const payload = await upstream.json().catch(() => null);
      if (!upstream.ok) {
        apiError(
          response,
          upstream.status,
          cleanText(payload?.error?.code, 120) || 'CONTROL_SERVER_ERROR',
          cleanText(payload?.error?.message, 1000) || 'Máy chủ điều phối từ chối yêu cầu.',
        );
        return;
      }

      // Job commands can return device, worker or launch metadata. The Order
      // Desk only needs an ACK; keep that internal state out of the browser.
      sendJson(response, upstream.status, { data: { accepted: true } });
    } catch {
      apiError(
        response,
        502,
        'CONTROL_SERVER_UNAVAILABLE',
        'Chưa kết nối được máy chủ điều phối. Hãy thử lại sau ít phút.',
      );
    }
  }

  async function serveLiveOrders(request, response, limit) {
    const adminKey = cleanText(request.headers['x-admin-key'], 4096);
    if (!adminKey) {
      apiError(response, 401, 'ADMIN_KEY_REQUIRED', 'Nhập Admin API key để tiếp tục.');
      return;
    }

    try {
      const upstream = await fetchImplementation(
        `${controlServerUrl}/api/admin/jobs?limit=${limit}&mode=course`,
        {
          method: 'GET',
          headers: { 'x-admin-key': adminKey, accept: 'application/json' },
          redirect: 'error',
          signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
        },
      );
      const payload = await upstream.json();
      if (!upstream.ok) {
        const errorCode = cleanText(payload?.error?.code, 120) || 'CONTROL_SERVER_ERROR';
        const errorMessage = cleanText(payload?.error?.message, 1000)
          || 'Máy chủ điều phối từ chối yêu cầu.';
        apiError(response, upstream.status, errorCode, errorMessage);
        return;
      }

      const jobs = Array.isArray(payload?.data)
        ? payload.data.map(mapLiveOrderJob).filter((job) => job?.mode === 'course')
        : [];
      sendJson(response, 200, {
        data: jobs,
        meta: { total: jobs.length, refreshedAt: new Date().toISOString() },
      });
    } catch {
      apiError(
        response,
        502,
        'CONTROL_SERVER_UNAVAILABLE',
        'Chưa lấy được trạng thái đơn từ máy chủ điều phối. Hệ thống sẽ tự thử lại.',
      );
    }
  }

  return createServer(async (request, response) => {
    applySecurityHeaders(response);
    const url = new URL(request.url || '/', 'http://order-site.local');

    try {
      if (request.method === 'GET' && url.pathname === '/health') {
        let backend = 'offline';
        let backendVersion = null;
        try {
          const upstream = await fetchImplementation(`${controlServerUrl}/health`, {
            headers: { accept: 'application/json' },
            signal: AbortSignal.timeout(4_000),
          });
          if (upstream.ok) {
            backend = 'online';
            const payload = await upstream.json().catch(() => null);
            backendVersion = payload?.data?.version ?? payload?.version ?? null;
          }
        } catch {
          // The order site remains healthy while the control server is restarting.
        }
        sendJson(response, 200, {
          data: { status: 'ok', service: 'acose-order-entry', backend, backendVersion },
        });
        return;
      }

      if (request.method === 'GET' && staticFiles.has(url.pathname)) {
        await serveStatic(response, publicDirectory, staticFiles.get(url.pathname));
        return;
      }

      if (request.method === 'GET' && url.pathname === '/api/courses') {
        await proxyAdminRequest(request, response, '/api/admin/courses?limit=200');
        return;
      }

      if (request.method === 'GET' && url.pathname === '/api/attempts') {
        const parsedLimit = Number.parseInt(url.searchParams.get('limit') || '12', 10);
        const limit = Math.min(100, Math.max(1, Number.isFinite(parsedLimit) ? parsedLimit : 12));
        await proxyAdminRequest(request, response, `/api/admin/direct-login-attempts?limit=${limit}`);
        return;
      }

      if (request.method === 'GET' && url.pathname === '/api/live-orders') {
        const parsedLimit = Number.parseInt(url.searchParams.get('limit') || '50', 10);
        const limit = Math.min(100, Math.max(1, Number.isFinite(parsedLimit) ? parsedLimit : 50));
        await serveLiveOrders(request, response, limit);
        return;
      }

      if (request.method === 'POST' && url.pathname === '/api/attempts') {
        const input = await readJsonBody(request);
        let safeOrder;
        try {
          safeOrder = validateDirectOrder(input);
          if (typeof input.googlePassword === 'string') input.googlePassword = '';
        } catch (error) {
          apiError(response, 400, error.code || 'INVALID_ORDER', error.message);
          return;
        }
        await proxyAdminRequest(request, response, '/api/admin/direct-login-attempts', safeOrder);
        safeOrder.googlePassword = '';
        return;
      }

      const openProfileMatch = url.pathname.match(
        /^\/api\/live-orders\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/open-profile$/i,
      );
      if (openProfileMatch && request.method === 'POST') {
        const input = await readJsonBody(request);
        const idempotencyKey = cleanText(input?.idempotencyKey, 200);
        if (!idempotencyKey) {
          apiError(response, 400, 'IDEMPOTENCY_KEY_REQUIRED', 'Thiếu mã chống mở trùng profile.');
          return;
        }
        await proxyJobActionRequest(
          request,
          response,
          `/api/admin/jobs/${encodeURIComponent(openProfileMatch[1])}/open-profile`,
          { idempotencyKey },
        );
        return;
      }

      const lifecycleMatch = url.pathname.match(
        /^\/api\/live-orders\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/(pause|resume)$/i,
      );
      if (lifecycleMatch && request.method === 'POST') {
        const action = lifecycleMatch[2].toLowerCase();
        await proxyJobActionRequest(
          request,
          response,
          `/api/admin/jobs/${encodeURIComponent(lifecycleMatch[1])}/${action}`,
        );
        return;
      }

      const attemptMatch = url.pathname.match(
        /^\/api\/attempts\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(\/cancel)?$/i,
      );
      if (attemptMatch && request.method === 'GET' && !attemptMatch[2]) {
        await proxyAdminRequest(
          request,
          response,
          `/api/admin/direct-login-attempts/${encodeURIComponent(attemptMatch[1])}`,
        );
        return;
      }
      if (attemptMatch && request.method === 'POST' && attemptMatch[2]) {
        await proxyAdminRequest(
          request,
          response,
          `/api/admin/direct-login-attempts/${encodeURIComponent(attemptMatch[1])}/cancel`,
          {},
        );
        return;
      }

      if (url.pathname.startsWith('/api/')) {
        apiError(response, 404, 'API_NOT_AVAILABLE', 'API này không được mở trên trang lên đơn.');
        return;
      }

      apiError(response, 404, 'NOT_FOUND', 'Không tìm thấy trang.');
    } catch (error) {
      if (error?.code === 'BODY_TOO_LARGE') {
        apiError(response, 413, error.code, error.message);
        return;
      }
      if (error?.code === 'INVALID_JSON') {
        apiError(response, 400, error.code, error.message);
        return;
      }
      apiError(response, 500, 'ORDER_SITE_ERROR', 'Trang lên đơn gặp lỗi tạm thời.');
    }
  });
}

const isMainModule = process.argv[1]
  && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url));

if (isMainModule) {
  const port = Number(process.env.PORT || 3000);
  const host = process.env.HOST || '0.0.0.0';
  const server = createOrderSiteServer();
  server.listen(port, host, () => {
    process.stdout.write(`ACOSE order entry listening on ${host}:${port}\n`);
  });
}
