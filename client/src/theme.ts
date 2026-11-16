import { DestroyRef, Injectable, inject, signal } from "@angular/core";

const storageKey = "paprika-theme";
const legacyStorageKey = "paperwork-theme";

@Injectable({ providedIn: "root" })
export class ThemeService {
  private system = window.matchMedia("(prefers-color-scheme: dark)");
  private preference: string | null = null;
  dark = signal(false);

  constructor() {
    this.preference = this.readPreference();
    this.apply();
    const changed = () => this.apply();
    const stored = (event: StorageEvent) => {
      if (event.key === storageKey || event.key === legacyStorageKey || event.key === null) { this.preference = this.readPreference(); this.apply(); }
    };
    this.system.addEventListener("change", changed);
    window.addEventListener("storage", stored);
    inject(DestroyRef).onDestroy(() => {
      this.system.removeEventListener("change", changed);
      window.removeEventListener("storage", stored);
    });
  }

  toggle() {
    this.preference = this.dark() ? "light" : "dark";
    try { localStorage.setItem(storageKey, this.preference); } catch { /* Keep the choice for this tab. */ }
    this.apply();
  }

  private readPreference(): string | null {
    try { return localStorage.getItem(storageKey) ?? localStorage.getItem(legacyStorageKey); }
    catch { return null; /* Use the system preference when storage is unavailable. */ }
  }

  private apply() {
    const dark = this.preference === "dark" || (this.preference !== "light" && this.system.matches);
    this.dark.set(dark);
    document.documentElement.dataset["theme"] = dark ? "dark" : "light";
  }
}
