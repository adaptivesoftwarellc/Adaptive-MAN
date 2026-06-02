// Node 26 ships an experimental global `localStorage` that is inert unless `--localstorage-file`
// is set, and it shadows jsdom's implementation. Install a simple in-memory Storage so the
// localStorage-backed code (usePageSize, api mock-mode) works under test.
class MemoryStorage {
  private store = new Map<string, string>();
  get length() {
    return this.store.size;
  }
  clear() {
    this.store.clear();
  }
  getItem(key: string) {
    return this.store.has(key) ? this.store.get(key)! : null;
  }
  setItem(key: string, value: string) {
    this.store.set(key, String(value));
  }
  removeItem(key: string) {
    this.store.delete(key);
  }
  key(index: number) {
    return Array.from(this.store.keys())[index] ?? null;
  }
}

const storage = new MemoryStorage();
try {
  Object.defineProperty(globalThis, 'localStorage', { value: storage, configurable: true, writable: true });
} catch {
  (globalThis as { localStorage?: unknown }).localStorage = storage;
}
