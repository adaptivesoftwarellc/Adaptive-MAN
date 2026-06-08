import { describe, it, expect } from 'vitest';
import { errorCategory, errorCategoryLabel, ERROR_CATEGORIES, EVENT_NAMES } from './catalog';

describe('errorCategory (mirrors the backend ingestion classifier)', () => {
  it('classifies a backend server error when exception_type is set', () => {
    expect(errorCategory({ exception_type: 'NullReferenceException', job_name: null })).toBe('server');
  });

  it('classifies a background job failure when only job_name is set', () => {
    expect(errorCategory({ exception_type: null, job_name: 'NightlySyncJob' })).toBe('background_job');
  });

  it('classifies a frontend exception when neither is set', () => {
    expect(errorCategory({ exception_type: null, job_name: null })).toBe('frontend');
  });

  it('checks exception_type before job_name (matches the classifier precedence)', () => {
    expect(errorCategory({ exception_type: 'TimeoutException', job_name: 'NightlySyncJob' })).toBe('server');
  });
});

describe('catalog shape', () => {
  it('exposes exactly the three error categories', () => {
    expect(ERROR_CATEGORIES.map((c) => c.value)).toEqual(['server', 'frontend', 'background_job']);
  });

  it('labels a known category and falls back to the raw value otherwise', () => {
    expect(errorCategoryLabel('server')).toBe('Backend (5xx)');
    expect(errorCategoryLabel('mystery')).toBe('mystery');
  });

  it('includes the failure event names the Health cards deep-link to', () => {
    expect(EVENT_NAMES).toContain('api_request_failed');
    expect(EVENT_NAMES).toContain('server_error_occurred');
    expect(EVENT_NAMES).toContain('page_viewed');
  });
});
