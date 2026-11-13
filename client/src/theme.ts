import { DestroyRef, Injectable, inject, signal } from "@angular/core";

const storageKey = "paperwork-theme";

@Injectable({ providedIn: "root" })
export class ThemeService {
  private system = window.matchMedia("(prefers-color-scheme: dark)");
  private preference: string | null = null;
  dark = signal(false);

  constructor() {
    try { this.preference = localStorage.getItem(storageKey); } catch { /* Use the system preference when storage is unavailable. */ }
    this.apply();
    const changed = () => this.apply();
    const stored = (event: StorageEvent) => {
      if (event.key === storageKey || event.key === null) { this.preference = event.newValue; this.apply(); }
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

  private apply() {
    const dark = this.preference === "dark" || (this.preference !== "light" && this.system.matches);
    this.dark.set(dark);
    document.documentElement.dataset["theme"] = dark ? "dark" : "light";
  }
}
