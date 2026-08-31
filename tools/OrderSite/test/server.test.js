import assert from 'node:assert/strict';
import { once } from 'node:events';
import test from 'node:test';
import { createOrderSiteServer } from '../server.js';

async function startServer(fetchImplementation) {
  const server = createOrderSiteServer({
    controlServerUrl: 'https://control.example',
    fetchImplementation,
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  return {
    server,
    baseUrl: `http://127.0.0.1:${address.port}`,
  };
}

async function closeServer(server) {
  server.close();
  await once(server, 'close');
}

function jsonResponse(payload, status = 200) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8' },
  });
}

test('serves the order surface with restrictive security headers', async () => {
  const { server, baseUrl } = await startServer(async () => jsonResponse({ data: { status: 'ok' } }));
  try {
    const response = await fetch(`${baseUrl}/`);
    const html = await response.text();
    assert.equal(response.status, 200);
    assert.match(html, /ACOSE Order Desk/);
    assert.equal(response.headers.get('x-frame-options'), 'DENY');
    assert.match(response.headers.get('content-security-policy'), /connect-src 'self'/);
    assert.equal(response.headers.get('referrer-policy'), 'no-referrer');
  } finally {
    await closeServer(server);
  }
});

test('health remains available and reports backend connectivity', async () => {
  const calls = [];
  const { server, baseUrl } = await startServer(async (url) => {
    calls.push(url);
    return jsonResponse({ data: { status: 'ok', version: '3.4.1' } });
  });
  try {
    const response = await fetch(`${baseUrl}/health`);
    const payload = await response.json();
    assert.equal(response.status, 200);
    assert.equal(payload.data.status, 'ok');
    assert.equal(payload.data.backend, 'online');
    assert.equal(payload.data.backendVersion, '3.4.1');
    assert.deepEqual(calls, ['https://control.example/health']);
  } finally {
    await closeServer(server);
  }
});

test('requires the admin key before forwarding an allowed API', async () => {
  let forwarded = false;
  const { server, baseUrl } = await startServer(async () => {
    forwarded = true;
    return jsonResponse({ data: [] });
  });
  try {
    const response = await fetch(`${baseUrl}/api/courses`);
    const payload = await response.json();
    assert.equal(response.status, 401);
    assert.equal(payload.error.code, 'ADMIN_KEY_REQUIRED');
    assert.equal(forwarded, false);
  } finally {
    await closeServer(server);
  }
});

test('forwards only the narrow course endpoint with the operator key', async () => {
  const calls = [];
  const { server, baseUrl } = await startServer(async (url, options) => {
    calls.push({ url, options });
    return jsonResponse({ data: [{ id: 'course-1', title: 'Course 1' }] });
  });
  try {
    const response = await fetch(`${baseUrl}/api/courses`, {
      headers: { 'x-admin-key': 'operator-secret' },
    });
    const payload = await response.json();
    assert.equal(response.status, 200);
    assert.equal(payload.data[0].id, 'course-1');
    assert.equal(calls[0].url, 'https://control.example/api/admin/courses?limit=200');
    assert.equal(calls[0].options.headers['x-admin-key'], 'operator-secret');
  } finally {
    await closeServer(server);
  }
});

test('requires the admin key before loading live orders', async () => {
  let forwarded = false;
  const { server, baseUrl } = await startServer(async () => {
    forwarded = true;
    return jsonResponse({ data: [] });
  });
  try {
    const response = await fetch(`${baseUrl}/api/live-orders`);
    const payload = await response.json();
    assert.equal(response.status, 401);
    assert.equal(payload.error.code, 'ADMIN_KEY_REQUIRED');
    assert.equal(forwarded, false);
  } finally {
    await closeServer(server);
  }
});

test('loads a clamped, whitelisted live-order feed from the control server', async () => {
  const calls = [];
  const { server, baseUrl } = await startServer(async (url, options) => {
    calls.push({ url, options });
    return jsonResponse({
      data: [{
        id: 'job-1',
        orderId: 'order-1',
        orderCode: 'ORD-001',
        customerCode: 'CUS-001',
        customerName: 'Khách A',
        courseTitle: 'Machine Learning',
        courseSlug: 'machine-learning',
        courseUrl: 'https://www.coursera.org/programs/acme-learning/learn/machine-learning',
        mode: 'course',
        status: 'running',
        progress: 42,
        currentModule: 2,
        totalModules: 5,
        currentActivity: 'Đang làm bài đọc',
        manualActionReason: null,
        errorMessageSafe: null,
        attempt: 1,
        lastHeartbeat: '2026-08-31T00:01:00.000Z',
        createdAt: '2026-08-31T00:00:01.000Z',
        updatedAt: '2026-08-31T00:01:00.000Z',
        workerId: 'secret-worker',
        deviceId: 'internal-device',
        publicDeviceId: 'desktop-id',
        courseraUserId: 'private-user',
        sessionExpiresAt: '2026-09-01T00:00:00.000Z',
        profileOpenable: true,
        pauseAllowed: true,
        pausePending: false,
        resumeAllowed: false,
        unexpectedSecret: 'DO_NOT_EXPOSE',
      }],
    });
  });
  try {
    const response = await fetch(`${baseUrl}/api/live-orders?limit=999`, {
      headers: { 'x-admin-key': 'operator-secret' },
    });
    const payload = await response.json();
    assert.equal(response.status, 200);
    assert.equal(response.headers.get('cache-control'), 'no-store');
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, 'https://control.example/api/admin/jobs?limit=100&mode=course');
    assert.equal(calls[0].options.method, 'GET');
    assert.equal(calls[0].options.headers['x-admin-key'], 'operator-secret');
    assert.equal(payload.data.length, 1);
    assert.deepEqual(payload.data[0], {
      id: 'job-1',
      orderId: 'order-1',
      orderCode: 'ORD-001',
      customerCode: 'CUS-001',
      customerName: 'Khách A',
      courseTitle: 'Machine Learning',
      courseSlug: 'machine-learning',
      courseUrl: 'https://www.coursera.org/programs/acme-learning/learn/machine-learning',
      mode: 'course',
      status: 'running',
      progress: 42,
      currentModule: 2,
      totalModules: 5,
      currentActivity: 'Đang làm bài đọc',
      manualActionReason: null,
      errorMessageSafe: null,
      attempt: 1,
      lastHeartbeat: '2026-08-31T00:01:00.000Z',
      startedAt: null,
      completedAt: null,
      createdAt: '2026-08-31T00:00:01.000Z',
      updatedAt: '2026-08-31T00:01:00.000Z',
      profileOpenable: true,
      pauseAllowed: true,
      pausePending: false,
      resumeAllowed: false,
    });
    assert.equal(JSON.stringify(payload).includes('secret-worker'), false);
    assert.equal(JSON.stringify(payload).includes('internal-device'), false);
    assert.equal(JSON.stringify(payload).includes('DO_NOT_EXPOSE'), false);
  } finally {
    await closeServer(server);
  }
});

test('forwards a job-scoped open-profile request and returns only a sanitized acknowledgement', async () => {
  const calls = [];
  const jobId = '22222222-2222-4222-8222-222222222222';
  const { server, baseUrl } = await startServer(async (url, options) => {
    calls.push({ url, options });
    return jsonResponse({
      data: {
        replayed: false,
        job: {
          id: 'browse-job-internal',
          mode: 'browse',
          status: 'queued',
          deviceId: 'internal-device-secret',
          customerId: 'internal-customer-secret',
          workerId: 'internal-worker-secret',
          courseraUserId: 'private-coursera-user',
          sessionStatus: 'ready',
        },
        order: { id: 'internal-order-secret' },
        launch: { workerId: 'internal-launch-worker-secret' },
      },
    }, 201);
  });
  try {
    const response = await fetch(`${baseUrl}/api/live-orders/${jobId}/open-profile`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-admin-key': 'operator-secret',
      },
      body: JSON.stringify({
        idempotencyKey: 'open-profile-once',
        deviceId: 'must-not-forward',
        profilePath: 'must-not-forward',
      }),
    });
    assert.equal(response.status, 201);
    const payload = await response.json();
    assert.deepEqual(payload, { data: { accepted: true } });
    assert.equal(JSON.stringify(payload).includes('internal-device-secret'), false);
    assert.equal(JSON.stringify(payload).includes('private-coursera-user'), false);
    assert.equal(JSON.stringify(payload).includes('internal-worker-secret'), false);
    assert.equal(calls.length, 1);
    assert.equal(
      calls[0].url,
      `https://control.example/api/admin/jobs/${jobId}/open-profile`,
    );
    assert.equal(calls[0].options.method, 'POST');
    assert.equal(calls[0].options.headers['x-admin-key'], 'operator-secret');
    assert.deepEqual(JSON.parse(calls[0].options.body), {
      idempotencyKey: 'open-profile-once',
    });
    assert.equal(calls[0].options.body.includes('must-not-forward'), false);
  } finally {
    await closeServer(server);
  }
});

test('validates open-profile authorization and idempotency before forwarding', async () => {
  let forwarded = false;
  const jobId = '33333333-3333-4333-8333-333333333333';
  const { server, baseUrl } = await startServer(async () => {
    forwarded = true;
    return jsonResponse({ data: {} }, 201);
  });
  try {
    const unauthorized = await fetch(`${baseUrl}/api/live-orders/${jobId}/open-profile`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ idempotencyKey: 'profile-auth-check' }),
    });
    assert.equal(unauthorized.status, 401);
    assert.equal((await unauthorized.json()).error.code, 'ADMIN_KEY_REQUIRED');

    const missingIdempotency = await fetch(`${baseUrl}/api/live-orders/${jobId}/open-profile`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-admin-key': 'operator-secret',
      },
      body: JSON.stringify({}),
    });
    assert.equal(missingIdempotency.status, 400);
    assert.equal((await missingIdempotency.json()).error.code, 'IDEMPOTENCY_KEY_REQUIRED');
    assert.equal(forwarded, false);
  } finally {
    await closeServer(server);
  }
});

test('forwards pause and resume commands without relaying browser-supplied fields', async () => {
  const calls = [];
  const jobId = '55555555-5555-4555-8555-555555555555';
  const { server, baseUrl } = await startServer(async (url, options) => {
    calls.push({ url, options });
    return jsonResponse({
      data: {
        accepted: true,
        deviceId: 'private-device',
        workerId: 'private-worker',
      },
    }, 202);
  });
  try {
    const unauthorized = await fetch(`${baseUrl}/api/live-orders/${jobId}/pause`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ workerId: 'must-not-forward' }),
    });
    assert.equal(unauthorized.status, 401);
    assert.equal((await unauthorized.json()).error.code, 'ADMIN_KEY_REQUIRED');

    for (const action of ['pause', 'resume']) {
      const response = await fetch(`${baseUrl}/api/live-orders/${jobId}/${action}`, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
          'x-admin-key': 'operator-secret',
        },
        body: JSON.stringify({
          deviceId: 'must-not-forward',
          workerId: 'must-not-forward',
          force: true,
        }),
      });
      assert.equal(response.status, 202);
      const payload = await response.json();
      assert.deepEqual(payload, { data: { accepted: true } });
      assert.equal(JSON.stringify(payload).includes('private-device'), false);
      assert.equal(JSON.stringify(payload).includes('private-worker'), false);
    }

    assert.equal(calls.length, 2);
    for (const [index, action] of ['pause', 'resume'].entries()) {
      assert.equal(
        calls[index].url,
        `https://control.example/api/admin/jobs/${jobId}/${action}`,
      );
      assert.equal(calls[index].options.method, 'POST');
      assert.equal(calls[index].options.headers['x-admin-key'], 'operator-secret');
      assert.equal(calls[index].options.headers['content-type'], undefined);
      assert.equal(calls[index].options.body, undefined);
    }
  } finally {
    await closeServer(server);
  }
});

test('sanitizes an upstream open-profile error instead of relaying extra metadata', async () => {
  const jobId = '44444444-4444-4444-8444-444444444444';
  const { server, baseUrl } = await startServer(async () => jsonResponse({
    error: {
      code: 'PROFILE_BUSY',
      message: 'Profile đang được worker sử dụng.',
      deviceId: 'private-device-in-error',
      workerId: 'private-worker-in-error',
    },
    debug: { profilePath: 'C:\\private\\profile' },
  }, 409));
  try {
    const response = await fetch(`${baseUrl}/api/live-orders/${jobId}/open-profile`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-admin-key': 'operator-secret',
      },
      body: JSON.stringify({ idempotencyKey: 'profile-busy-check' }),
    });
    const payload = await response.json();
    assert.equal(response.status, 409);
    assert.deepEqual(payload, {
      error: {
        code: 'PROFILE_BUSY',
        message: 'Profile đang được worker sử dụng.',
      },
    });
    assert.equal(JSON.stringify(payload).includes('private-device-in-error'), false);
    assert.equal(JSON.stringify(payload).includes('private-worker-in-error'), false);
  } finally {
    await closeServer(server);
  }
});

test('uses the live-order default limit for invalid input and preserves upstream auth errors', async () => {
  const calls = [];
  const { server, baseUrl } = await startServer(async (url) => {
    calls.push(url);
    return jsonResponse({ error: { code: 'ADMIN_UNAUTHORIZED', message: 'Key đã hết hiệu lực.' } }, 401);
  });
  try {
    const response = await fetch(`${baseUrl}/api/live-orders?limit=invalid`, {
      headers: { 'x-admin-key': 'operator-secret' },
    });
    const payload = await response.json();
    assert.equal(response.status, 401);
    assert.equal(calls[0], 'https://control.example/api/admin/jobs?limit=50&mode=course');
    assert.deepEqual(payload, {
      error: { code: 'ADMIN_UNAUTHORIZED', message: 'Key đã hết hiệu lực.' },
    });
    assert.equal(JSON.stringify(payload).includes('operator-secret'), false);
  } finally {
    await closeServer(server);
  }
});

test('sanitizes direct-order fields and forwards credentials only to the control server', async () => {
  const calls = [];
  const attemptId = '11111111-1111-4111-8111-111111111111';
  const { server, baseUrl } = await startServer(async (url, options) => {
    calls.push({ url, options });
    return jsonResponse({ data: { id: attemptId, status: 'queued' } }, 201);
  });
  try {
    const response = await fetch(`${baseUrl}/api/attempts`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-admin-key': 'operator-secret',
      },
      body: JSON.stringify({
        customerName: '  Khách A  ',
        courses: [{
          courseUrl: 'https://www.coursera.org/programs/acme-learning/learn/course-one',
        }],
        googleEmail: ' customer@example.com ',
        googlePassword: 'exact-password',
        skipGradedAppItems: false,
        skipPracticeAppItems: true,
        idempotencyKey: 'retry-1',
        workerKey: 'must-not-pass',
        unexpectedAdminAction: true,
      }),
    });
    assert.equal(response.status, 201);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, 'https://control.example/api/admin/direct-login-attempts');
    const forwarded = JSON.parse(calls[0].options.body);
    assert.deepEqual(forwarded, {
      customerName: 'Khách A',
      courses: [{
        courseUrl: 'https://www.coursera.org/programs/acme-learning/learn/course-one',
      }],
      googleEmail: 'customer@example.com',
      googlePassword: 'exact-password',
      skipGradedAppItems: false,
      skipPracticeAppItems: true,
      idempotencyKey: 'retry-1',
    });
  } finally {
    await closeServer(server);
  }
});

test('does not expose unrelated admin or worker APIs', async () => {
  let forwarded = false;
  const { server, baseUrl } = await startServer(async () => {
    forwarded = true;
    return jsonResponse({ data: {} });
  });
  try {
    for (const path of ['/api/admin/settings', '/api/admin/jobs', '/api/admin/orders']) {
      const response = await fetch(`${baseUrl}${path}`, {
        headers: { 'x-admin-key': 'operator-secret' },
      });
      const payload = await response.json();
      assert.equal(response.status, 404);
      assert.equal(payload.error.code, 'API_NOT_AVAILABLE');
    }
    const postResponse = await fetch(`${baseUrl}/api/live-orders`, {
      method: 'POST',
      headers: { 'x-admin-key': 'operator-secret' },
    });
    const postPayload = await postResponse.json();
    assert.equal(postResponse.status, 404);
    assert.equal(postPayload.error.code, 'API_NOT_AVAILABLE');
    assert.equal(forwarded, false);
  } finally {
    await closeServer(server);
  }
});
