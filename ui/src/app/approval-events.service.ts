import { Injectable } from '@angular/core';
import { Subject } from 'rxjs';

export interface ApprovalResult {
  id: string;
  result: string;
}

@Injectable({ providedIn: 'root' })
export class ApprovalEventsService {
  private readonly completedSubject = new Subject<ApprovalResult>();
  readonly completed$ = this.completedSubject.asObservable();

  completed(result: ApprovalResult): void {
    this.completedSubject.next(result);
  }
}
