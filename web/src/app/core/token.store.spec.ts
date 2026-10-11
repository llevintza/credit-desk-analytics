import { TestBed } from '@angular/core/testing';
import { REFRESH_TOKEN_KEY, TokenStore } from './token.store';

const tokens = { tokenType: 'Bearer', accessToken: 'access', expiresIn: 300, refreshToken: 'refresh' };

describe('TokenStore (ADR-0024)', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    sessionStorage.clear();
  });

  it('keeps the access token in memory and only the refresh token in sessionStorage', () => {
    const store = TestBed.inject(TokenStore);
    expect(store.hasSession()).toBe(false);
    store.set(tokens);
    expect(store.accessToken()).toBe('access');
    expect(store.refreshToken()).toBe('refresh');
    expect(store.hasSession()).toBe(true);
    expect(sessionStorage.getItem(REFRESH_TOKEN_KEY)).toBe('refresh');
    expect(Object.values({ ...sessionStorage })).not.toContain('access');

    store.clear();
    expect(store.accessToken()).toBeNull();
    expect(store.hasSession()).toBe(false);
    expect(sessionStorage.getItem(REFRESH_TOKEN_KEY)).toBeNull();
  });

  it('picks up the refresh token a reload left in this tab', () => {
    sessionStorage.setItem(REFRESH_TOKEN_KEY, 'left-behind');
    const store = TestBed.inject(TokenStore);
    expect(store.accessToken()).toBeNull();
    expect(store.refreshToken()).toBe('left-behind');
    expect(store.hasSession()).toBe(true);
  });

  it('works for this page when storage is blocked', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => { throw new Error('blocked'); });
    const store = TestBed.inject(TokenStore);
    expect(store.refreshToken()).toBeNull();
    store.set(tokens);
    expect(store.refreshToken()).toBe('refresh');
    store.clear();
    expect(store.hasSession()).toBe(false);
  });
});
