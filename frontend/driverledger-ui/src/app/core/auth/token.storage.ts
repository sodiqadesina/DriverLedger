export class TokenStorage {
  private static readonly KEY = 'dl_token';

  static get(): string | null {
    return localStorage.getItem(TokenStorage.KEY);
  }

  static set(token: string): void {
    localStorage.setItem(TokenStorage.KEY, token);
  }

  static clear(): void {
    localStorage.removeItem(TokenStorage.KEY);
  }
}
