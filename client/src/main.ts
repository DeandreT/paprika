import { bootstrapApplication } from "@angular/platform-browser";
import { provideZonelessChangeDetection } from "@angular/core";
import { provideHttpClient } from "@angular/common/http";
import { AppComponent } from "./app";
bootstrapApplication(AppComponent, {
  providers: [provideZonelessChangeDetection(), provideHttpClient()],
}).catch(console.error);
