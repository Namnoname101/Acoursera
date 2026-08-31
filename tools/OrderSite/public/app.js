const TERMINAL_STATUSES = new Set([
  'completed',
  'manual_required',
  'failed',
  'cancelled',
  'expired',
]);

const ACTIVE_JOB_STATUSES = new Set(['starting', 'running']);
const LIVE_JOB_STATUSES = new Set(['queued', 'starting', 'running', 'waiting_user']);
const JOB_SIGNAL_LATE_MS = 90_000;
const LIVE_ORDERS_LIMIT = 50;
const LIVE_POLL_ACTIVE_MS = 5_000;
const LIVE_POLL_IDLE_MS = 15_000;
const PROFILE_OPEN_COOLDOWN_MS = 30_000;
const JOB_LIFECYCLE_COOLDOWN_MS = 15_000;

const STATUS_COPY = {
  queued: ['Đang chờ', 'Đã tiếp nhận yêu cầu', 'Worker máy chủ sẽ nhận phiên trong ít phút.'],
  claimed: ['Worker đã nhận', 'Đang chuẩn bị trình duyệt', 'Một hồ sơ đăng nhập riêng đang được khởi tạo.'],
  signing_in: ['Đang đăng nhập', 'Đang đăng nhập Google', 'Hệ thống đang kiểm tra phiên Coursera.'],
  waiting_number: ['Cần xác minh', 'Khách cần chọn số', 'Mở điện thoại và chọn đúng số đang hiển thị.'],
  waiting_approval: ['Cần xác minh', 'Khách cần bấm Yes / Có', 'Mở thông báo Google hoặc Gmail và xác nhận.'],
  completed: ['Hoàn tất', 'Tạo đơn thành công', 'Các khóa học đã được đưa vào hàng đợi.'],
  manual_required: ['Cần thao tác', 'Không thể tự động xác minh', 'Google yêu cầu một bước đăng nhập thủ công.'],
  failed: ['Có lỗi', 'Đăng nhập thất bại', 'Phiên đã dừng và chưa tạo đơn.'],
  cancelled: ['Đã hủy', 'Phiên đăng nhập đã hủy', 'Thông tin đăng nhập tạm thời đã được xóa.'],
  expired: ['Hết hạn', 'Phiên đăng nhập đã hết hạn', 'Hãy tạo một phiên mới để thử lại.'],
};

const STATUS_LABELS = {
  queued: 'Đang chờ',
  claimed: 'Worker đã nhận',
  signing_in: 'Đang đăng nhập',
  waiting_number: 'Chờ chọn số',
  waiting_approval: 'Chờ bấm Yes',
  completed: 'Hoàn tất',
  manual_required: 'Cần thủ công',
  failed: 'Có lỗi',
  cancelled: 'Đã hủy',
  expired: 'Hết hạn',
  running: 'Đang chạy',
  starting: 'Đang khởi động',
  waiting_user: 'Cần thao tác',
  timeout: 'Hết thời gian',
  pending: 'Đang chờ',
};

const state = {
  key: sessionStorage.getItem('acose_order_admin_key') || '',
  courses: [],
  selectedCourseIds: new Set(),
  attempts: [],
  liveOrders: [],
  liveOrdersError: '',
  liveOrdersUpdatedAt: null,
  liveOrdersTimer: null,
  liveOrdersRequest: null,
  liveOrdersFailures: 0,
  // Keep commands outside the rendered cards. Polling replaces every card,
  // so button-local state alone can otherwise submit the same open request
  // again while the first request is still in flight.
  profileOpenCommands: new Map(),
  jobLifecycleCommands: new Map(),
  highlightedJobIds: new Set(),
  highlightedOrderIds: new Set(),
  announcedAttemptIds: new Set(),
  liveRefreshAttemptId: null,
  currentAttempt: null,
  idempotencyKey: crypto.randomUUID(),
  pollTimer: null,
  pollGeneration: 0,
};

const elements = {
  connectionState: document.querySelector('#connectionState'),
  changeKeyButton: document.querySelector('#changeKeyButton'),
  orderForm: document.querySelector('#orderForm'),
  orderFields: document.querySelector('#orderFields'),
  customerName: document.querySelector('#customerName'),
  courseSearch: document.querySelector('#courseSearch'),
  courseList: document.querySelector('#courseList'),
  courseUrls: document.querySelector('#courseUrls'),
  selectionCount: document.querySelector('#selectionCount'),
  skipGraded: document.querySelector('#skipGraded'),
  skipPractice: document.querySelector('#skipPractice'),
  googleEmail: document.querySelector('#googleEmail'),
  googlePassword: document.querySelector('#googlePassword'),
  togglePassword: document.querySelector('#togglePassword'),
  formError: document.querySelector('#formError'),
  submitOrder: document.querySelector('#submitOrder'),
  emptyStatus: document.querySelector('#emptyStatus'),
  activeStatus: document.querySelector('#activeStatus'),
  liveBadge: document.querySelector('#liveBadge'),
  statusIcon: document.querySelector('#statusIcon'),
  statusCode: document.querySelector('#statusCode'),
  statusHeading: document.querySelector('#statusHeading'),
  statusDescription: document.querySelector('#statusDescription'),
  statusTimeline: document.querySelector('#statusTimeline'),
  challengeCard: document.querySelector('#challengeCard'),
  challengeLabel: document.querySelector('#challengeLabel'),
  challengeValue: document.querySelector('#challengeValue'),
  challengeHelp: document.querySelector('#challengeHelp'),
  resultCard: document.querySelector('#resultCard'),
  resultOrders: document.querySelector('#resultOrders'),
  resultCustomer: document.querySelector('#resultCustomer'),
  resultCourses: document.querySelector('#resultCourses'),
  resultJob: document.querySelector('#resultJob'),
  activityText: document.querySelector('#activityText'),
  activityMeta: document.querySelector('#activityMeta'),
  newOrderButton: document.querySelector('#newOrderButton'),
  cancelAttemptButton: document.querySelector('#cancelAttemptButton'),
  viewLiveOrdersButton: document.querySelector('#viewLiveOrdersButton'),
  liveOrdersPanel: document.querySelector('#liveOrdersPanel'),
  liveOrdersTitle: document.querySelector('#liveOrdersTitle'),
  jobsLiveBadge: document.querySelector('#jobsLiveBadge'),
  liveOrderSummary: document.querySelector('#liveOrderSummary'),
  liveRunningCount: document.querySelector('#liveRunningCount'),
  liveQueuedCount: document.querySelector('#liveQueuedCount'),
  liveAttentionCount: document.querySelector('#liveAttentionCount'),
  liveFinishedCount: document.querySelector('#liveFinishedCount'),
  liveOrdersNotice: document.querySelector('#liveOrdersNotice'),
  liveOrdersList: document.querySelector('#liveOrdersList'),
  refreshLiveOrders: document.querySelector('#refreshLiveOrders'),
  refreshAttempts: document.querySelector('#refreshAttempts'),
  recentAttempts: document.querySelector('#recentAttempts'),
  authDialog: document.querySelector('#authDialog'),
  authForm: document.querySelector('#authForm'),
  adminKeyInput: document.querySelector('#adminKeyInput'),
  authError: document.querySelector('#authError'),
  connectButton: document.querySelector('#connectButton'),
  toastRegion: document.querySelector('#toastRegion'),
};

function statusLabel(status) {
  return STATUS_LABELS[status] || String(status || 'Không xác định').replaceAll('_', ' ');
}

function formatRelativeTime(value) {
  const timestamp = new Date(value || 0).getTime();
  if (!Number.isFinite(timestamp) || timestamp <= 0) return 'Chưa có thời gian';
  const seconds = Math.max(0, Math.floor((Date.now() - timestamp) / 1000));
  if (seconds < 5) return 'Vừa xong';
  if (seconds < 60) return `${seconds} giây trước`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} phút trước`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} giờ trước`;
  return `${Math.floor(hours / 24)} ngày trước`;
}

function createNode(tagName, className = '', text = '') {
  const node = document.createElement(tagName);
  if (className) node.className = className;
  if (text !== '') node.textContent = text;
  return node;
}

function safeProgress(value) {
  const progress = Number(value);
  if (!Number.isFinite(progress)) return 0;
  return Math.round(Math.min(100, Math.max(0, progress)));
}

function jobSignalTime(job) {
  return job.lastHeartbeat || job.updatedAt || job.startedAt || job.createdAt || null;
}

function isJobSignalLate(job) {
  if (!ACTIVE_JOB_STATUSES.has(job.status)) return false;
  const signal = new Date(jobSignalTime(job) || 0).getTime();
  return Number.isFinite(signal) && signal > 0 && Date.now() - signal > JOB_SIGNAL_LATE_MS;
}

function liveJobGroup(job) {
  if (isJobSignalLate(job) || ['waiting_user', 'failed', 'timeout'].includes(job.status)) {
    return 'attention';
  }
  if (ACTIVE_JOB_STATUSES.has(job.status)) return 'active';
  if (job.status === 'queued') return 'queued';
  return 'finished';
}

function liveJobLabel(job) {
  if (isJobSignalLate(job)) return 'Trễ tín hiệu';
  const labels = {
    queued: 'Chờ worker',
    starting: 'Đang khởi động',
    running: 'Đang xử lý',
    waiting_user: 'Cần thao tác',
    completed: 'Hoàn tất',
    failed: 'Cần kiểm tra',
    timeout: 'Cần kiểm tra',
    cancelled: 'Đã hủy',
  };
  return labels[job.status] || statusLabel(job.status);
}

function coursePathLabel(courseUrl, courseSlug = '') {
  try {
    const parsed = new URL(courseUrl);
    if (parsed.protocol === 'https:' &&
        ['coursera.org', 'www.coursera.org'].includes(parsed.hostname.toLowerCase()) &&
        parsed.pathname !== '/') {
      return parsed.pathname.replace(/\/$/, '');
    }
  } catch {
    // Fall back to the stored label below.
  }
  return courseSlug ? courseSlug : 'Coursera course';
}

function getProfileOpenCommand(jobId) {
  let command = state.profileOpenCommands.get(jobId);
  if (!command) {
    command = {
      idempotencyKey: crypto.randomUUID(),
      pending: false,
      confirmedUntil: 0,
      cleanupTimer: null,
    };
    state.profileOpenCommands.set(jobId, command);
  }
  return command;
}

function profileOpenButtonState(jobId) {
  const command = state.profileOpenCommands.get(jobId);
  if (command?.pending) return { disabled: true, text: 'Đang mở…' };
  if (command?.confirmedUntil > Date.now()) return { disabled: true, text: 'Đã gửi lệnh' };
  return { disabled: false, text: 'Mở profile' };
}

async function openOrderProfile(job) {
  if (!job?.id || !job.profileOpenable) return;
  const command = getProfileOpenCommand(job.id);
  if (command.pending || command.confirmedUntil > Date.now()) return;

  command.pending = true;
  renderLiveOrders();
  try {
    await api(`/api/live-orders/${encodeURIComponent(job.id)}/open-profile`, {
      method: 'POST',
      body: JSON.stringify({ idempotencyKey: command.idempotencyKey }),
    });
    command.pending = false;
    command.confirmedUntil = Date.now() + PROFILE_OPEN_COOLDOWN_MS;
    if (command.cleanupTimer) clearTimeout(command.cleanupTimer);
    command.cleanupTimer = setTimeout(() => {
      if (state.profileOpenCommands.get(job.id) === command) {
        state.profileOpenCommands.delete(job.id);
        renderLiveOrders();
      }
    }, PROFILE_OPEN_COOLDOWN_MS);
    renderLiveOrders();
    toast(
      'Đang mở profile',
      'Cửa sổ Coursera riêng của tài khoản sẽ xuất hiện trên máy Worker Host.',
    );
    refreshLiveOrders().catch(() => {});
  } catch (error) {
    // Reuse the same idempotency key on retry. A timeout can happen after the
    // control server accepted the command but before this browser saw the ACK.
    command.pending = false;
    renderLiveOrders();
    toast('Chưa mở được profile', error.message, true);
  }
}

function lifecycleButtonState(jobId, action, defaultText) {
  const command = state.jobLifecycleCommands.get(jobId);
  if (!command || (command.confirmedUntil <= Date.now() && !command.pending)) {
    return { disabled: false, text: defaultText };
  }
  if (command.action !== action) return { disabled: true, text: defaultText };
  if (action === 'pause') {
    return { disabled: true, text: command.pending ? 'Đang gửi…' : 'Đang tạm dừng…' };
  }
  return { disabled: true, text: command.pending ? 'Đang gửi…' : 'Đang tiếp tục…' };
}

function clearLifecycleCommand(jobId, command) {
  if (state.jobLifecycleCommands.get(jobId) !== command) return;
  if (command.cleanupTimer) clearTimeout(command.cleanupTimer);
  state.jobLifecycleCommands.delete(jobId);
}

async function runJobLifecycleCommand(job, action) {
  if (!job?.id || !['pause', 'resume'].includes(action)) return;
  if (action === 'pause' && (!job.pauseAllowed || job.pausePending)) return;
  if (action === 'resume' && !job.resumeAllowed) return;

  const current = state.jobLifecycleCommands.get(job.id);
  if (current?.pending || current?.confirmedUntil > Date.now()) return;

  const command = {
    action,
    pending: true,
    confirmedUntil: 0,
    cleanupTimer: null,
  };
  state.jobLifecycleCommands.set(job.id, command);
  renderLiveOrders();

  try {
    await api(`/api/live-orders/${encodeURIComponent(job.id)}/${action}`, { method: 'POST' });
    command.pending = false;
    command.confirmedUntil = Date.now() + JOB_LIFECYCLE_COOLDOWN_MS;
    renderLiveOrders();
    toast(
      action === 'pause' ? 'Đã gửi lệnh tạm dừng' : 'Đã gửi lệnh tiếp tục',
      action === 'pause'
        ? 'Profile sẽ tạm dừng an toàn sau bước hiện tại.'
        : 'Profile sẽ được đưa lại vào hàng chờ xử lý.',
    );

    const refreshed = await refreshLiveOrders();
    const updatedJob = state.liveOrders.find((item) => item.id === job.id);
    const transitionObserved = action === 'pause'
      ? updatedJob?.pausePending || updatedJob?.resumeAllowed || updatedJob?.pauseAllowed === false
      : updatedJob?.pauseAllowed || updatedJob?.resumeAllowed === false;
    if (refreshed && transitionObserved) {
      clearLifecycleCommand(job.id, command);
      renderLiveOrders();
      return;
    }

    command.cleanupTimer = setTimeout(() => {
      clearLifecycleCommand(job.id, command);
      renderLiveOrders();
      refreshLiveOrders().catch(() => {});
    }, JOB_LIFECYCLE_COOLDOWN_MS);
  } catch (error) {
    clearLifecycleCommand(job.id, command);
    renderLiveOrders();
    toast(
      action === 'pause' ? 'Chưa tạm dừng được' : 'Chưa tiếp tục được',
      error.message,
      true,
    );
  }
}

function jobModuleText(job) {
  const hasCurrent = job.currentModule !== null && job.currentModule !== undefined;
  const hasTotal = job.totalModules !== null && job.totalModules !== undefined;
  const current = hasCurrent ? Number(job.currentModule) : Number.NaN;
  const total = hasTotal ? Number(job.totalModules) : Number.NaN;
  if (Number.isFinite(current) && Number.isFinite(total) && total > 0) {
    return `Module ${current}/${total}`;
  }
  if (Number.isFinite(total) && total > 0) return `${total} module`;
  return 'Đang nhận diện cấu trúc';
}

function jobActivityText(job) {
  if (['waiting_user', 'failed', 'timeout'].includes(job.status) || isJobSignalLate(job)) {
    return job.errorMessageSafe
      || job.manualActionReason
      || job.currentActivity
      || 'Đơn cần được kiểm tra trên máy chủ điều phối.';
  }
  if (job.status === 'queued') return job.currentActivity || 'Đang chờ tới lượt xử lý.';
  if (job.status === 'starting') return job.currentActivity || 'Worker đang khởi động phiên làm việc.';
  if (job.status === 'completed') return job.currentActivity || 'Khóa học đã được xử lý hoàn tất.';
  if (job.status === 'cancelled') return job.currentActivity || 'Đơn đã được hủy.';
  return job.currentActivity || 'Worker đang cập nhật tiến độ.';
}

function liveOrderPriority(job) {
  const priorities = { active: 0, attention: 1, queued: 2, finished: 3 };
  return priorities[liveJobGroup(job)] ?? 4;
}

function renderLiveOrders() {
  const jobs = [...state.liveOrders].sort((left, right) => {
    const priorityDifference = liveOrderPriority(left) - liveOrderPriority(right);
    if (priorityDifference) return priorityDifference;
    return new Date(right.updatedAt || right.createdAt || 0).getTime()
      - new Date(left.updatedAt || left.createdAt || 0).getTime();
  });

  const counts = { active: 0, queued: 0, attention: 0, finished: 0 };
  for (const job of jobs) counts[liveJobGroup(job)] += 1;
  elements.liveRunningCount.textContent = String(counts.active);
  elements.liveQueuedCount.textContent = String(counts.queued);
  elements.liveAttentionCount.textContent = String(counts.attention);
  elements.liveFinishedCount.textContent = String(counts.finished);

  elements.liveOrdersNotice.hidden = !state.liveOrdersError;
  if (state.liveOrdersError) {
    const lastGood = state.liveOrdersUpdatedAt
      ? ` Dữ liệu gần nhất ${formatRelativeTime(state.liveOrdersUpdatedAt)}.`
      : '';
    elements.liveOrdersNotice.textContent = `${state.liveOrdersError}${lastGood} Hệ thống sẽ tự thử lại.`;
  }

  elements.liveOrdersList.replaceChildren();
  elements.liveOrdersList.setAttribute('aria-busy', 'false');
  if (!jobs.length) {
    const empty = createNode('div', 'live-orders-empty');
    const heading = createNode(
      'h3',
      '',
      state.liveOrdersError ? 'Chưa tải được trạng thái đơn' : 'Chưa có đơn đang theo dõi',
    );
    const copy = createNode(
      'p',
      '',
      state.liveOrdersError
        ? 'Kiểm tra kết nối rồi bấm Làm mới để thử lại.'
        : 'Sau khi đăng nhập thành công, tiến độ từng khóa sẽ tự xuất hiện tại đây.',
    );
    empty.append(heading, copy);
    elements.liveOrdersList.appendChild(empty);
    return;
  }

  for (const job of jobs) {
    const group = liveJobGroup(job);
    const progress = safeProgress(job.progress);
    const article = createNode('article', `live-order-card ${group}`);
    if (state.highlightedJobIds.has(job.id) || state.highlightedOrderIds.has(job.orderId)) {
      article.classList.add('newly-created');
    }
    article.setAttribute(
      'aria-label',
      `${job.orderCode || 'Đơn'} · ${job.courseTitle || job.courseSlug || 'Khóa học'} · ${liveJobLabel(job)}`,
    );

    const top = createNode('div', 'live-order-top');
    const identity = createNode('div', 'live-order-identity');
    identity.append(
      createNode('strong', '', job.orderCode || 'Đơn chưa có mã'),
      createNode(
        'small',
        '',
        [job.customerName || 'Khách chưa đặt tên', job.customerCode].filter(Boolean).join(' · '),
      ),
    );
    const status = createNode('span', `job-status ${group}`, liveJobLabel(job));
    top.append(identity, status);

    const course = createNode('div', 'live-order-course');
    course.append(
      createNode('h3', '', job.courseTitle || job.courseSlug || 'Đang nhận diện khóa học'),
      createNode('small', '', coursePathLabel(job.courseUrl, job.courseSlug)),
    );

    const progressHeading = createNode('div', 'live-order-progress-heading');
    progressHeading.append(
      createNode('span', '', jobModuleText(job)),
      createNode('strong', '', `${progress}%`),
    );
    const progressElement = document.createElement('progress');
    progressElement.className = 'live-order-progress';
    progressElement.max = 100;
    progressElement.value = progress;
    progressElement.setAttribute(
      'aria-label',
      `Tiến độ ${job.courseTitle || job.courseSlug || 'khóa học'}: ${progress}%`,
    );

    const activity = createNode('p', 'live-order-activity', jobActivityText(job));
    const footer = createNode('div', 'live-order-footer');
    const footerMeta = createNode('div', 'live-order-footer-meta');
    const signalTime = jobSignalTime(job);
    const signal = createNode(
      'time',
      '',
      `${ACTIVE_JOB_STATUSES.has(job.status) ? 'Heartbeat' : 'Cập nhật'} ${formatRelativeTime(signalTime)}`,
    );
    if (signalTime) signal.dateTime = signalTime;
    footerMeta.appendChild(signal);
    if (Number(job.attempt) > 1) {
      footerMeta.appendChild(createNode('span', '', `Lần chạy ${job.attempt}`));
    }
    footer.appendChild(footerMeta);
    const footerActions = createNode('div', 'live-order-footer-actions');
    if (job.pausePending) {
      const pausePendingButton = createNode(
        'button',
        'button secondary job-lifecycle-button pause',
        'Đang tạm dừng…',
      );
      pausePendingButton.type = 'button';
      pausePendingButton.disabled = true;
      pausePendingButton.setAttribute('aria-label', 'Profile đang chờ tạm dừng an toàn');
      footerActions.appendChild(pausePendingButton);
    } else if (job.pauseAllowed) {
      const buttonState = lifecycleButtonState(job.id, 'pause', 'Tạm dừng');
      const pauseButton = createNode(
        'button',
        'button secondary job-lifecycle-button pause',
        buttonState.text,
      );
      pauseButton.type = 'button';
      pauseButton.disabled = buttonState.disabled;
      pauseButton.setAttribute(
        'aria-label',
        `Tạm dừng profile của ${job.customerName || job.orderCode || 'đơn này'}`,
      );
      pauseButton.addEventListener('click', () => runJobLifecycleCommand(job, 'pause'));
      footerActions.appendChild(pauseButton);
    }
    if (job.resumeAllowed) {
      const buttonState = lifecycleButtonState(job.id, 'resume', 'Tiếp tục');
      const resumeButton = createNode(
        'button',
        'button secondary job-lifecycle-button resume',
        buttonState.text,
      );
      resumeButton.type = 'button';
      resumeButton.disabled = buttonState.disabled;
      resumeButton.setAttribute(
        'aria-label',
        `Tiếp tục profile của ${job.customerName || job.orderCode || 'đơn này'}`,
      );
      resumeButton.addEventListener('click', () => runJobLifecycleCommand(job, 'resume'));
      footerActions.appendChild(resumeButton);
    }
    if (job.profileOpenable) {
      const buttonState = profileOpenButtonState(job.id);
      const openProfileButton = createNode(
        'button',
        'button secondary profile-open-button',
        buttonState.text,
      );
      openProfileButton.type = 'button';
      openProfileButton.disabled = buttonState.disabled;
      openProfileButton.setAttribute(
        'aria-label',
        `Mở profile Coursera của ${job.customerName || job.orderCode || 'đơn này'}`,
      );
      openProfileButton.addEventListener('click', () => {
        openOrderProfile(job);
      });
      footerActions.appendChild(openProfileButton);
    }
    if (footerActions.childElementCount) footer.appendChild(footerActions);

    article.append(top, course, progressHeading, progressElement, activity, footer);
    elements.liveOrdersList.appendChild(article);
  }
}

function updateLiveBadge(mode = 'ready', delay = LIVE_POLL_ACTIVE_MS) {
  const copy = elements.jobsLiveBadge.querySelector('span');
  elements.jobsLiveBadge.classList.toggle('stale', mode === 'error' || mode === 'auth');
  if (mode === 'refreshing') copy.textContent = 'Đang cập nhật';
  else if (mode === 'error') copy.textContent = 'Đang thử lại';
  else if (mode === 'auth') copy.textContent = 'Chờ kết nối';
  else copy.textContent = `Cập nhật ${Math.round(delay / 1000)} giây`;
}

function nextLivePollDelay() {
  if (state.liveOrdersFailures) {
    return Math.min(30_000, LIVE_POLL_ACTIVE_MS * (2 ** (state.liveOrdersFailures - 1)));
  }
  return state.liveOrders.some((job) => LIVE_JOB_STATUSES.has(job.status))
    ? LIVE_POLL_ACTIVE_MS
    : LIVE_POLL_IDLE_MS;
}

function stopLiveOrdersPolling() {
  if (state.liveOrdersTimer) clearTimeout(state.liveOrdersTimer);
  state.liveOrdersTimer = null;
}

function scheduleLiveOrdersPoll(delay = nextLivePollDelay()) {
  stopLiveOrdersPolling();
  if (!state.key || document.hidden) return;
  updateLiveBadge(state.liveOrdersError ? 'error' : 'ready', delay);
  state.liveOrdersTimer = setTimeout(() => refreshLiveOrders(), delay);
}

async function refreshLiveOrders({ showLoading = false } = {}) {
  stopLiveOrdersPolling();
  if (!state.key || document.hidden) return false;
  if (state.liveOrdersRequest) return state.liveOrdersRequest;

  if (showLoading && !state.liveOrders.length) {
    elements.liveOrdersList.setAttribute('aria-busy', 'true');
  }
  updateLiveBadge('refreshing');
  state.liveOrdersRequest = (async () => {
    try {
      const response = await api(`/api/live-orders?limit=${LIVE_ORDERS_LIMIT}`);
      state.liveOrders = Array.isArray(response.data) ? response.data : [];
      state.liveOrdersUpdatedAt = response.meta?.refreshedAt || new Date().toISOString();
      state.liveOrdersError = '';
      state.liveOrdersFailures = 0;
      renderLiveOrders();
      return true;
    } catch (error) {
      state.liveOrdersError = error.message || 'Tạm thời mất kết nối với máy chủ.';
      state.liveOrdersFailures += 1;
      renderLiveOrders();
      if (error.status === 401) {
        state.key = '';
        stopLiveOrdersPolling();
        updateLiveBadge('auth');
      }
      return false;
    }
  })();

  const succeeded = await state.liveOrdersRequest;
  state.liveOrdersRequest = null;
  if (state.key && !document.hidden) scheduleLiveOrdersPoll();
  return succeeded;
}

function registerCreatedOrders(attempt) {
  const result = attempt.result || {};
  const jobs = result.jobs || (result.job ? [result.job] : []);
  const orders = result.orders || (result.order ? [result.order] : []);
  state.highlightedJobIds = new Set(jobs.map((job) => job?.id).filter(Boolean));
  state.highlightedOrderIds = new Set([
    ...jobs.map((job) => job?.orderId),
    ...orders.map((order) => order?.id || order?.orderId),
  ].filter(Boolean));
}

function toast(title, message = '', isError = false) {
  const item = document.createElement('div');
  item.className = `toast${isError ? ' error' : ''}`;
  const heading = document.createElement('strong');
  heading.textContent = title;
  item.appendChild(heading);
  if (message) {
    const copy = document.createElement('span');
    copy.textContent = message;
    item.appendChild(copy);
  }
  elements.toastRegion.appendChild(item);
  setTimeout(() => item.remove(), 4500);
}

function showAuth(message = '') {
  elements.authError.textContent = message;
  elements.adminKeyInput.value = state.key;
  if (!elements.authDialog.open) elements.authDialog.showModal();
  setTimeout(() => elements.adminKeyInput.focus(), 0);
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: {
      accept: 'application/json',
      'content-type': 'application/json',
      'x-admin-key': state.key,
      ...(options.headers || {}),
    },
  });
  const payload = await response.json().catch(() => null);
  if (!response.ok) {
    const error = new Error(payload?.error?.message || `Yêu cầu thất bại (${response.status}).`);
    error.status = response.status;
    error.code = payload?.error?.code || '';
    if (response.status === 401 && !options.suppressAuth) {
      sessionStorage.removeItem('acose_order_admin_key');
      showAuth('Admin API key không đúng hoặc đã thay đổi.');
    }
    throw error;
  }
  return payload;
}

async function refreshHealth() {
  try {
    const response = await fetch('/health', { headers: { accept: 'application/json' } });
    const payload = await response.json();
    const backendOnline = response.ok && payload?.data?.backend === 'online';
    elements.connectionState.className = `connection-state ${backendOnline ? 'online' : 'offline'}`;
    elements.connectionState.querySelector('span').textContent = backendOnline
      ? 'Máy chủ online'
      : 'Máy chủ đang ngắt';
  } catch {
    elements.connectionState.className = 'connection-state offline';
    elements.connectionState.querySelector('span').textContent = 'Site đang ngắt';
  }
}

function parseCourseUrls() {
  return [...new Set(
    elements.courseUrls.value
      .split(/\r?\n/)
      .map((value) => value.trim())
      .filter(Boolean),
  )];
}

function updateSelectionCount() {
  const total = state.selectedCourseIds.size + parseCourseUrls().length;
  elements.selectionCount.textContent = `${total} khóa`;
  elements.selectionCount.classList.toggle('has-selection', total > 0);
}

function renderCourses() {
  const query = elements.courseSearch.value.trim().toLocaleLowerCase('vi');
  const visible = state.courses.filter((course) => {
    const haystack = `${course.title || ''} ${course.slug || ''}`.toLocaleLowerCase('vi');
    return !query || haystack.includes(query);
  });
  elements.courseList.replaceChildren();

  if (!visible.length) {
    const empty = document.createElement('div');
    empty.className = 'empty-row';
    const copy = document.createElement('p');
    copy.textContent = state.courses.length
      ? 'Không có khóa học khớp từ khóa.'
      : 'Chưa có khóa học đã lưu. Bạn vẫn có thể dán URL bên dưới.';
    empty.appendChild(copy);
    elements.courseList.appendChild(empty);
    return;
  }

  for (const course of visible) {
    const label = document.createElement('label');
    label.className = 'course-option';

    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.value = course.id;
    checkbox.checked = state.selectedCourseIds.has(course.id);
    checkbox.addEventListener('change', () => {
      if (checkbox.checked) state.selectedCourseIds.add(course.id);
      else state.selectedCourseIds.delete(course.id);
      updateSelectionCount();
    });

    const copy = document.createElement('span');
    const title = document.createElement('strong');
    title.textContent = course.title || course.slug || 'Coursera course';
    const slug = document.createElement('small');
    slug.textContent = coursePathLabel(course.canonicalUrl || course.url, course.slug || '—');
    copy.append(title, slug);

    const tag = document.createElement('em');
    tag.textContent = 'Đã lưu';
    label.append(checkbox, copy, tag);
    elements.courseList.appendChild(label);
  }
}

async function loadCourses() {
  const response = await api('/api/courses');
  state.courses = (response.data || []).filter((course) => course.status === 'active');
  renderCourses();
}

function attemptCourseSummary(attempt) {
  const courses = attempt.courses || (attempt.course ? [attempt.course] : []);
  return courses
    .map((course) => course?.title || course?.slug)
    .filter(Boolean)
    .join(' · ') || 'Đang nhận diện khóa học';
}

function attemptDotClass(status) {
  if (status === 'completed') return 'completed';
  if (TERMINAL_STATUSES.has(status)) return 'failed';
  return 'active';
}

function renderAttempts() {
  elements.recentAttempts.replaceChildren();
  if (!state.attempts.length) {
    const empty = document.createElement('div');
    empty.className = 'empty-row';
    const copy = document.createElement('p');
    copy.textContent = 'Chưa có phiên lên đơn nào trong bộ nhớ máy chủ.';
    empty.appendChild(copy);
    elements.recentAttempts.appendChild(empty);
    return;
  }

  for (const attempt of state.attempts.slice(0, 12)) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'attempt-item';
    button.setAttribute('aria-label', `Mở phiên ${attempt.customerName || attempt.loginHint || 'gần đây'}`);

    const dot = document.createElement('span');
    dot.className = `attempt-dot ${attemptDotClass(attempt.status)}`;
    const copy = document.createElement('span');
    copy.className = 'attempt-copy';
    const name = document.createElement('strong');
    name.textContent = attempt.customerName || attempt.loginHint || 'Khách mới';
    const detail = document.createElement('small');
    detail.textContent = `${statusLabel(attempt.status)} · ${attemptCourseSummary(attempt)}`;
    copy.append(name, detail);
    const time = document.createElement('time');
    time.textContent = formatRelativeTime(attempt.updatedAt || attempt.createdAt);
    button.append(dot, copy, time);
    button.addEventListener('click', () => monitorAttempt(attempt.id));
    elements.recentAttempts.appendChild(button);
  }
}

async function loadAttempts() {
  const response = await api('/api/attempts?limit=12');
  state.attempts = response.data || [];
  renderAttempts();
  return state.attempts;
}

function attemptStage(attempt) {
  if (attempt.status === 'completed') return 4;
  if (['waiting_number', 'waiting_approval'].includes(attempt.status)) return 3;
  if (['claimed', 'signing_in'].includes(attempt.status)) return 2;
  if (['manual_required', 'failed'].includes(attempt.status)) {
    return attempt.challengeNumber ? 3 : 2;
  }
  return 1;
}

function setFormLocked(locked, label = '') {
  elements.orderFields.disabled = locked;
  elements.submitOrder.disabled = locked;
  elements.submitOrder.querySelector('span').textContent = label || 'Đăng nhập & tạo đơn';
}

function renderAttempt(attempt) {
  state.currentAttempt = attempt;
  sessionStorage.setItem('acose_order_attempt_id', attempt.id);
  elements.emptyStatus.hidden = true;
  elements.activeStatus.hidden = false;

  const terminal = TERMINAL_STATUSES.has(attempt.status);
  const success = attempt.status === 'completed';
  const failed = terminal && !success;
  const [code, heading, description] = STATUS_COPY[attempt.status] || [
    'Đang xử lý',
    'Máy chủ đang cập nhật',
    'Vui lòng chờ trạng thái tiếp theo.',
  ];

  elements.liveBadge.hidden = terminal;
  elements.statusCode.textContent = code;
  elements.statusHeading.textContent = heading;
  elements.statusDescription.textContent = description;
  elements.statusIcon.className = `status-icon${success ? ' success' : ''}${failed ? ' error' : ''}`;

  const currentStage = attemptStage(attempt);
  for (const item of elements.statusTimeline.querySelectorAll('li')) {
    const stage = Number(item.dataset.stage);
    item.className = stage < currentStage || success
      ? 'done'
      : (stage === currentStage ? (failed ? 'error' : 'current') : '');
  }

  const waitingForNumber = attempt.status === 'waiting_number' && attempt.challengeNumber;
  const waitingForApproval = attempt.status === 'waiting_approval';
  elements.challengeCard.hidden = !waitingForNumber && !waitingForApproval;
  if (waitingForNumber) {
    elements.challengeLabel.textContent = 'Số khách cần chọn';
    elements.challengeValue.textContent = attempt.challengeNumber;
    elements.challengeHelp.textContent = 'Báo khách chọn đúng số này trên điện thoại. Không yêu cầu OTP hoặc mật khẩu.';
  } else if (waitingForApproval) {
    elements.challengeLabel.textContent = 'Khách cần xác nhận';
    elements.challengeValue.textContent = 'YES / CÓ';
    elements.challengeHelp.textContent = 'Báo khách mở thông báo Google hoặc Gmail và bấm Yes/Có. Không yêu cầu OTP.';
  }

  elements.resultCard.hidden = !success;
  if (success) {
    const result = attempt.result || {};
    const orders = result.orders || (result.order ? [result.order] : []);
    elements.resultOrders.textContent = orders.length
      ? `${orders.length} đơn`
      : (result.order?.orderCode || 'Đã tạo');
    elements.resultCustomer.textContent = result.customer?.name || attempt.customerName || 'Khách mới';
    elements.resultCourses.textContent = attemptCourseSummary(attempt);
    elements.resultJob.textContent = orders.length
      ? `${orders.length} đơn · xem bên dưới`
      : statusLabel(result.job?.status || 'queued');
  }

  elements.activityText.textContent = failed
    ? (attempt.errorMessageSafe || attempt.manualActionReason || attempt.activity || description)
    : (attempt.activity || description);
  const account = attempt.loginHint ? `Tài khoản ${attempt.loginHint} · ` : '';
  elements.activityMeta.textContent = `${account}${formatRelativeTime(attempt.updatedAt || attempt.createdAt)}`;
  elements.cancelAttemptButton.hidden = terminal;
  elements.newOrderButton.hidden = !terminal;
  elements.viewLiveOrdersButton.hidden = !success;
  setFormLocked(!terminal, terminal ? '' : 'Đang xử lý phiên hiện tại…');

  if (terminal) {
    stopPolling();
    loadAttempts().catch(() => {});
    if (success && state.liveRefreshAttemptId !== attempt.id) {
      state.liveRefreshAttemptId = attempt.id;
      registerCreatedOrders(attempt);
      refreshLiveOrders().catch(() => {});
    }
    if (success && !state.announcedAttemptIds.has(attempt.id)) {
      state.announcedAttemptIds.add(attempt.id);
      toast('Đã tạo đơn thành công', attemptCourseSummary(attempt));
    }
  }
}

function stopPolling() {
  if (state.pollTimer) clearTimeout(state.pollTimer);
  state.pollTimer = null;
  state.pollGeneration += 1;
}

function schedulePoll(attemptId, generation, delay = 1400) {
  if (state.pollTimer) clearTimeout(state.pollTimer);
  state.pollTimer = setTimeout(() => pollAttempt(attemptId, generation), delay);
}

async function pollAttempt(attemptId, generation) {
  if (generation !== state.pollGeneration) return;
  try {
    const response = await api(`/api/attempts/${encodeURIComponent(attemptId)}`);
    if (generation !== state.pollGeneration) return;
    renderAttempt(response.data);
    if (!TERMINAL_STATUSES.has(response.data.status)) schedulePoll(attemptId, generation);
  } catch (error) {
    if (generation !== state.pollGeneration) return;
    if ([404, 410].includes(error.status)) {
      renderAttempt({
        id: attemptId,
        status: 'expired',
        activity: 'Phiên không còn trên máy chủ. Hãy tạo lại từ đầu.',
        updatedAt: new Date().toISOString(),
      });
      return;
    }
    elements.activityText.textContent = `Tạm thời chưa lấy được trạng thái: ${error.message}`;
    schedulePoll(attemptId, generation, 3000);
  }
}

async function monitorAttempt(attemptId) {
  stopPolling();
  elements.emptyStatus.hidden = true;
  elements.activeStatus.hidden = false;
  elements.activityText.textContent = 'Đang lấy trạng thái mới nhất…';
  try {
    const response = await api(`/api/attempts/${encodeURIComponent(attemptId)}`);
    renderAttempt(response.data);
    if (!TERMINAL_STATUSES.has(response.data.status)) {
      const generation = state.pollGeneration;
      schedulePoll(attemptId, generation);
    }
  } catch (error) {
    toast('Không mở được phiên', error.message, true);
  }
}

function resetForNewOrder() {
  stopPolling();
  state.currentAttempt = null;
  state.idempotencyKey = crypto.randomUUID();
  state.selectedCourseIds.clear();
  sessionStorage.removeItem('acose_order_attempt_id');
  elements.orderForm.reset();
  elements.skipGraded.checked = true;
  elements.skipPractice.checked = true;
  elements.googlePassword.value = '';
  elements.googlePassword.type = 'password';
  elements.togglePassword.textContent = 'Hiện';
  elements.emptyStatus.hidden = false;
  elements.activeStatus.hidden = true;
  elements.viewLiveOrdersButton.hidden = true;
  elements.formError.textContent = '';
  setFormLocked(false);
  renderCourses();
  updateSelectionCount();
  elements.customerName.focus();
}

elements.orderForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  if (state.currentAttempt && !TERMINAL_STATUSES.has(state.currentAttempt.status)) {
    elements.formError.textContent = 'Phiên hiện tại vẫn đang xử lý. Hãy hoàn tất hoặc hủy phiên trước.';
    return;
  }

  const typedUrls = parseCourseUrls();
  const courses = [
    ...[...state.selectedCourseIds].map((courseId) => ({ courseId })),
    ...typedUrls.map((courseUrl) => ({ courseUrl })),
  ];
  if (!courses.length) {
    elements.formError.textContent = 'Chọn ít nhất một khóa học đã lưu hoặc dán URL khóa học.';
    elements.courseList.scrollIntoView({ behavior: 'smooth', block: 'center' });
    return;
  }
  if (courses.length > 20) {
    elements.formError.textContent = 'Mỗi phiên chỉ nhận tối đa 20 khóa học.';
    return;
  }

  const googleEmail = elements.googleEmail.value.trim();
  const googlePassword = elements.googlePassword.value;
  if (!googleEmail || !googlePassword) {
    elements.formError.textContent = 'Nhập đủ tài khoản và mật khẩu Google.';
    return;
  }

  const payload = {
    customerName: elements.customerName.value.trim() || undefined,
    courses,
    googleEmail,
    googlePassword,
    skipGradedAppItems: elements.skipGraded.checked,
    skipPracticeAppItems: elements.skipPractice.checked,
    idempotencyKey: state.idempotencyKey,
  };
  let requestBody = JSON.stringify(payload);
  payload.googlePassword = '';
  elements.googlePassword.value = '';
  elements.formError.className = 'form-message info';
  elements.formError.textContent = 'Đang gửi thông tin trực tiếp tới worker máy chủ…';
  setFormLocked(true, 'Đang gửi lên máy chủ…');

  try {
    const response = await api('/api/attempts', { method: 'POST', body: requestBody });
    requestBody = '';
    elements.googleEmail.value = '';
    elements.formError.textContent = '';
    elements.formError.className = 'form-message';
    state.idempotencyKey = crypto.randomUUID();
    renderAttempt(response.data);
    const generation = state.pollGeneration;
    if (!TERMINAL_STATUSES.has(response.data.status)) {
      schedulePoll(response.data.id, generation, 700);
    }
    await loadAttempts().catch(() => {});
  } catch (error) {
    requestBody = '';
    if (error.code === 'DIRECT_LOGIN_IDEMPOTENCY_MISMATCH') {
      state.idempotencyKey = crypto.randomUUID();
      elements.formError.textContent = 'Thông tin đã đổi so với lần gửi trước. Đã tạo mã thử mới; nhập lại mật khẩu và gửi lại.';
    } else {
      elements.formError.textContent = `${error.message} Mật khẩu đã được xóa; hãy nhập lại để thử tiếp.`;
    }
    elements.formError.className = 'form-message';
    setFormLocked(false);
    elements.googlePassword.focus();
  }
});

elements.authForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  const candidate = elements.adminKeyInput.value.trim();
  if (!candidate) return;
  const previousKey = state.key;
  state.key = candidate;
  elements.connectButton.disabled = true;
  elements.connectButton.textContent = 'Đang kết nối…';
  elements.authError.textContent = '';
  try {
    await Promise.all([loadCourses(), loadAttempts(), refreshLiveOrders({ showLoading: true })]);
    sessionStorage.setItem('acose_order_admin_key', candidate);
    elements.adminKeyInput.value = '';
    elements.authDialog.close();
    toast('Đã kết nối', 'Order Desk đã sẵn sàng để lên đơn.');
  } catch (error) {
    state.key = previousKey;
    elements.authError.textContent = error.status === 401
      ? 'Admin API key không đúng.'
      : error.message;
  } finally {
    elements.connectButton.disabled = false;
    elements.connectButton.textContent = 'Kết nối máy chủ';
  }
});

elements.authDialog.addEventListener('cancel', (event) => {
  if (!state.key) event.preventDefault();
});

elements.changeKeyButton.addEventListener('click', () => {
  stopPolling();
  stopLiveOrdersPolling();
  state.key = '';
  state.liveOrders = [];
  state.liveOrdersError = '';
  state.liveOrdersUpdatedAt = null;
  for (const command of state.profileOpenCommands.values()) {
    if (command.cleanupTimer) clearTimeout(command.cleanupTimer);
  }
  state.profileOpenCommands.clear();
  for (const command of state.jobLifecycleCommands.values()) {
    if (command.cleanupTimer) clearTimeout(command.cleanupTimer);
  }
  state.jobLifecycleCommands.clear();
  renderLiveOrders();
  updateLiveBadge('auth');
  sessionStorage.removeItem('acose_order_admin_key');
  showAuth();
});

elements.courseSearch.addEventListener('input', renderCourses);
elements.courseUrls.addEventListener('input', updateSelectionCount);
elements.refreshAttempts.addEventListener('click', async () => {
  elements.refreshAttempts.disabled = true;
  try {
    await loadAttempts();
  } catch (error) {
    toast('Chưa làm mới được', error.message, true);
  } finally {
    elements.refreshAttempts.disabled = false;
  }
});

elements.refreshLiveOrders.addEventListener('click', async () => {
  elements.refreshLiveOrders.disabled = true;
  try {
    await refreshLiveOrders({ showLoading: !state.liveOrders.length });
  } finally {
    elements.refreshLiveOrders.disabled = false;
  }
});

elements.togglePassword.addEventListener('click', () => {
  const reveal = elements.googlePassword.type === 'password';
  elements.googlePassword.type = reveal ? 'text' : 'password';
  elements.togglePassword.textContent = reveal ? 'Ẩn' : 'Hiện';
  elements.togglePassword.setAttribute('aria-label', reveal ? 'Ẩn mật khẩu' : 'Hiện mật khẩu');
});

elements.cancelAttemptButton.addEventListener('click', async () => {
  const attempt = state.currentAttempt;
  if (!attempt || TERMINAL_STATUSES.has(attempt.status)) return;
  if (!window.confirm('Hủy phiên đăng nhập này? Thông tin tạm thời sẽ bị xóa và chưa tạo đơn.')) return;
  elements.cancelAttemptButton.disabled = true;
  try {
    const response = await api(`/api/attempts/${encodeURIComponent(attempt.id)}/cancel`, {
      method: 'POST',
      body: '{}',
    });
    renderAttempt(response.data);
    toast('Đã hủy phiên', 'Chưa tạo đơn; thông tin đăng nhập tạm thời đã được xóa.');
  } catch (error) {
    toast('Không thể hủy phiên', error.message, true);
  } finally {
    elements.cancelAttemptButton.disabled = false;
  }
});

elements.newOrderButton.addEventListener('click', resetForNewOrder);
elements.viewLiveOrdersButton.addEventListener('click', () => {
  elements.liveOrdersPanel.scrollIntoView({ behavior: 'smooth', block: 'start' });
  elements.liveOrdersTitle.focus({ preventScroll: true });
});

window.addEventListener('pagehide', () => {
  stopLiveOrdersPolling();
  elements.googlePassword.value = '';
  elements.adminKeyInput.value = '';
});

document.addEventListener('visibilitychange', () => {
  if (document.hidden) {
    stopLiveOrdersPolling();
    return;
  }
  refreshLiveOrders().catch(() => {});
  if (state.currentAttempt && !TERMINAL_STATUSES.has(state.currentAttempt.status)) {
    monitorAttempt(state.currentAttempt.id);
  }
});

async function initialize() {
  await refreshHealth();
  setInterval(refreshHealth, 30_000);
  if (!state.key) {
    renderLiveOrders();
    updateLiveBadge('auth');
    showAuth();
    return;
  }

  try {
    const [, attempts] = await Promise.all([
      loadCourses(),
      loadAttempts(),
      refreshLiveOrders({ showLoading: true }),
    ]);
    const rememberedAttemptId = sessionStorage.getItem('acose_order_attempt_id');
    if (rememberedAttemptId) {
      const remembered = attempts.find((attempt) => attempt.id === rememberedAttemptId);
      if (remembered) {
        renderAttempt(remembered);
        if (!TERMINAL_STATUSES.has(remembered.status)) {
          const generation = state.pollGeneration;
          schedulePoll(remembered.id, generation, 500);
        }
      }
    }
  } catch (error) {
    if (error.status !== 401) showAuth(error.message);
  }
}

initialize();
